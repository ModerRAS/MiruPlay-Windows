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
        Func<long, CancellationToken, Task<CloudDriveIngestionSummary>>? rescanWebDav = null)
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
    }

    public CloudDriveRunStatus Status => _status;

    public async Task<CloudDriveRunStatus> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("CloudDrive/RSS 同步已在运行。");
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _status = new CloudDriveRunStatus("RUNNING", true, startedAt);
        try
        {
            var summary = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            var finishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var config = _config.Load();
            _config.Save(config with { LastRunAt = finishedAt });
            _status = new CloudDriveRunStatus("SUCCEEDED", false, startedAt, finishedAt, summary);
            return _status;
        }
        catch (Exception error)
        {
            _status = new CloudDriveRunStatus(
                "FAILED",
                false,
                startedAt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error: error.Message);
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
        if (!config.Enabled) throw new InvalidOperationException("请先启用 CloudDrive/RSS 同步。");
        var savedCredentials = _credentials.LoadForEndpoint(config.EndpointUrl);
        var token = savedCredentials.Token;
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("请先登录或验证 CloudDrive2 API Token。");
        CloudDriveTokenInfo info;
        try
        {
            info = await _cloudDrive.GetApiTokenInfoAsync(config.EndpointUrl, token, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (IsAuthenticationFailure(error))
        {
            (token, info) = await RefreshAuthenticationAsync(config, savedCredentials.Password, cancellationToken).ConfigureAwait(false);
        }
        if (!info.AllowAddOfflineDownload) throw new InvalidOperationException("CloudDrive2 API Token 没有离线下载权限。");
        var root = NormalizePath(info.RootDir);
        var inbox = NormalizePath(config.InboxPath);
        if (inbox == "/" || !IsWithinRoot(inbox, root)) throw new InvalidOperationException("CloudDrive2 离线下载目录超出 Token 根目录或指向根目录。");
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
            throw new InvalidOperationException("CloudDrive2 认证已过期，且没有可用于重新登录的已保存凭据。");
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
        if (segments.Any(segment => segment is "." or "..")) throw new InvalidOperationException("CloudDrive2 目录包含路径遍历。");
        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private static bool IsWithinRoot(string path, string root) =>
        root == "/" || path == root || path.StartsWith($"{root}/", StringComparison.Ordinal);
}
