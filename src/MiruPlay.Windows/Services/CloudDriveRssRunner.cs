using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record CloudDriveRunSummary(
    int Submitted,
    int Skipped,
    int Failed,
    int Organized = 0,
    int Indexed = 0,
    int Scraped = 0,
    int NoMatch = 0);

public sealed record CloudDriveIngestionSummary(int Indexed = 0, int Scraped = 0, int NoMatch = 0);

public sealed record CloudDriveRunStatus(
    string Status,
    bool Running,
    long StartedAt = 0,
    long FinishedAt = 0,
    CloudDriveRunSummary? Summary = null,
    string? Error = null);

public sealed class CloudDriveRssRunner
{
    private readonly CloudDriveAutomationStore _config;
    private readonly CloudDriveCredentialStore _credentials;
    private readonly RssSubscriptionStore _subscriptions;
    private readonly RssFeedClient _feedClient;
    private readonly RssProcessedStore _processed;
    private readonly CloudDriveGrpcClient _cloudDrive;
    private readonly TorrentSubmissionPreparer _torrentPreparer;
    private readonly CloudDriveLibraryOrganizer _organizer;
    private readonly Func<long, CancellationToken, Task<CloudDriveIngestionSummary>>? _rescanWebDav;
    private readonly RotatingLocalLogStore? _localLogs;
    private int _running;
    private CloudDriveRunStatus _status = new("IDLE", false);

    public CloudDriveRssRunner(
        CloudDriveAutomationStore config,
        CloudDriveCredentialStore credentials,
        RssSubscriptionStore subscriptions,
        RssFeedClient feedClient,
        RssProcessedStore processed,
        CloudDriveGrpcClient cloudDrive,
        TorrentSubmissionPreparer? torrentPreparer = null,
        CloudDriveLibraryOrganizer? organizer = null,
        Func<long, CancellationToken, Task<CloudDriveIngestionSummary>>? rescanWebDav = null,
        RotatingLocalLogStore? localLogs = null)
    {
        _config = config;
        _credentials = credentials;
        _subscriptions = subscriptions;
        _feedClient = feedClient;
        _processed = processed;
        _cloudDrive = cloudDrive;
        _torrentPreparer = torrentPreparer ?? new TorrentSubmissionPreparer(cloudDrive);
        _organizer = organizer ?? new CloudDriveLibraryOrganizer(cloudDrive);
        _rescanWebDav = rescanWebDav;
        _localLogs = localLogs;
    }

    public CloudDriveRunStatus Status => _status;

    public async Task<CloudDriveRunStatus> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException(AppStrings.Get("runner.syncRunning"));
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _status = new CloudDriveRunStatus("RUNNING", true, startedAt);
        _localLogs?.Write("INFO", "CloudDrive/RSS run started");
        try
        {
            var summary = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            var finishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var config = _config.Load();
            _config.Save(config with { LastRunAt = finishedAt });
            _status = new CloudDriveRunStatus("SUCCEEDED", false, startedAt, finishedAt, summary);
            _localLogs?.Write("INFO", "CloudDrive/RSS run completed", attributes: new Dictionary<string, string>
            {
                ["submitted"] = summary.Submitted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["skipped"] = summary.Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failed"] = summary.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["organized"] = summary.Organized.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["indexed"] = summary.Indexed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return _status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status = new CloudDriveRunStatus("CANCELED", false, startedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _localLogs?.Write("WARN", "CloudDrive/RSS run canceled");
            throw;
        }
        catch (Exception error)
        {
            _status = new CloudDriveRunStatus(
                "FAILED",
                false,
                startedAt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error: RotatingLocalLogStore.Redact(error.Message));
            _localLogs?.Write("ERROR", "CloudDrive/RSS run failed", error);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<CloudDriveRunSummary> RunCoreAsync(CancellationToken cancellationToken)
    {
        var config = _config.Load();
        if (!config.Enabled) throw new InvalidOperationException(AppStrings.Get("runner.enableFirst"));
        var savedCredentials = _credentials.LoadForEndpoint(config.EndpointUrl);
        var token = savedCredentials.Token;
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException(AppStrings.Get("api.errCdLogin"));
        CloudDriveTokenInfo info;
        try
        {
            info = await _cloudDrive.GetApiTokenInfoAsync(config.EndpointUrl, token, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (IsAuthenticationFailure(error))
        {
            (token, info) = await RefreshAuthenticationAsync(config, savedCredentials.Password, cancellationToken).ConfigureAwait(false);
        }
        if (!info.AllowAddOfflineDownload) throw new InvalidOperationException(AppStrings.Get("api.errCdNoOffline"));
        var root = NormalizePath(info.RootDir);
        var inbox = NormalizePath(config.InboxPath);
        if (inbox == "/" || !IsWithinRoot(inbox, root)) throw new InvalidOperationException(AppStrings.Get("runner.inboxOutside"));
        var submitted = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var subscription in _subscriptions.List().Where(item => item.Enabled))
        {
            IReadOnlyList<RssFeedItem> items;
            try
            {
                items = await _feedClient.FetchAsync(
                    subscription.Url,
                    config.RssProxyEnabled,
                    config.RssProxyHost,
                    config.RssProxyPort,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or System.Xml.XmlException or TimeoutException)
            {
                failed++;
                continue;
            }
            IReadOnlyList<RssSubmissionDecision> decisions;
            try
            {
                decisions = RssSubmissionPlanner.Plan(items, subscription.FilterRegex);
            }
            catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
            {
                failed++;
                continue;
            }
            foreach (var decision in decisions)
            {
                if (decision.Status == RssSubmissionStatus.SkippedFilter)
                {
                    skipped++;
                    continue;
                }
                if (decision.Status == RssSubmissionStatus.MissingSubmission || decision.SubmissionUrl is null || decision.ItemKey is null)
                {
                    failed++;
                    continue;
                }
                if (_processed.IsProcessed(subscription.Id, decision.ItemKey))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var submissionUrl = IsTorrent(decision.SubmissionUrl)
                        ? await _torrentPreparer.PrepareAsync(config, info, token, decision, cancellationToken).ConfigureAwait(false)
                        : decision.SubmissionUrl;
                    try
                    {
                        await _cloudDrive.AddOfflineFilesAsync(
                            config.EndpointUrl,
                            token,
                            [submissionUrl],
                            inbox,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException error) when (IsAuthenticationFailure(error))
                    {
                        (token, info) = await RefreshAuthenticationAsync(config, savedCredentials.Password, cancellationToken).ConfigureAwait(false);
                        await _cloudDrive.AddOfflineFilesAsync(
                            config.EndpointUrl,
                            token,
                            [submissionUrl],
                            inbox,
                            cancellationToken).ConfigureAwait(false);
                    }
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _processed.MarkSubmitted(new RssProcessedItem(
                        subscription.Id,
                        decision.ItemKey,
                        decision.Item.Title,
                        decision.SubmissionUrl,
                        now));
                    submitted++;
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or ArgumentException or InvalidDataException)
                {
                    failed++;
                }
            }
            _subscriptions.MarkChecked(subscription.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        var organized = config.LibraryMode == CloudDriveLibraryMode.OrganizedLibrary
            ? await _organizer.OrganizeAsync(config, info, token, cancellationToken).ConfigureAwait(false)
            : 0;
        var ingestion = new CloudDriveIngestionSummary();
        if (config.WebDavSourceId is long webDavSourceId && _rescanWebDav is not null)
        {
            ingestion = await _rescanWebDav(webDavSourceId, cancellationToken).ConfigureAwait(false);
        }
        return new CloudDriveRunSummary(submitted, skipped, failed, organized, ingestion.Indexed, ingestion.Scraped, ingestion.NoMatch);
    }

    private async Task<(string Token, CloudDriveTokenInfo Info)> RefreshAuthenticationAsync(
        CloudDriveAutomationConfig config,
        string? password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(AppStrings.Get("runner.authExpired"));
        var login = await _cloudDrive.LoginAsync(config.EndpointUrl, config.Username, password, cancellationToken).ConfigureAwait(false);
        var info = await _cloudDrive.GetApiTokenInfoAsync(config.EndpointUrl, login.Token, cancellationToken).ConfigureAwait(false);
        _credentials.SaveToken(config.EndpointUrl, login.Token);
        return (login.Token, info);
    }

    private static bool IsAuthenticationFailure(HttpRequestException error) =>
        error.InnerException is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unauthenticated } ||
        error.Message.Contains("(Unauthenticated)", StringComparison.OrdinalIgnoreCase);

    private static bool IsTorrent(string url)
    {
        var separator = url.IndexOfAny('?', '#');
        return url.AsSpan(0, separator >= 0 ? separator : url.Length).EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var segments = path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) throw new InvalidOperationException(AppStrings.Get("api.errTraversal"));
        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private static bool IsWithinRoot(string path, string root) =>
        root == "/" || path == root || path.StartsWith($"{root}/", StringComparison.Ordinal);
}
