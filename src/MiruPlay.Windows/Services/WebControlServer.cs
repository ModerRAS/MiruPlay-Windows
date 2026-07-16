using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record PlaybackControlCommand(
    string Command,
    long? PositionMs = null,
    long? DeltaMs = null,
    float? Speed = null,
    int? SubtitleTrackId = null);

public sealed record MediaSourceActions(
    Func<IReadOnlyList<MediaSourceInfoDto>> List,
    Func<MediaSourceRequest, Task<SourceTestResponse>> Test,
    Func<MediaSourceRequest, Task<MediaSourceInfoDto>> Add,
    Func<long, MediaSourceRequest, Task<MediaSourceInfoDto>> Update,
    Func<long, Task> Remove,
    Func<long, Task<SourceScanResponse>> Scan);

public sealed record PlaybackSubtitleTrack(
    int Id,
    string Language,
    string Title,
    string Codec,
    bool IsExternal,
    string? ExternalFileName,
    bool IsSelected)
{
    public string DisplayLabel
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Title)
                ? (string.IsNullOrWhiteSpace(Language) || Language == "und" ? $"字幕 {Id}" : Language)
                : Title;
            return IsExternal ? $"{label}（外挂）" : label;
        }
    }
}

public sealed record PlaybackRuntimeStatus(
    string State = "IDLE",
    string? Uri = null,
    string? EpisodeId = null,
    string? Title = null,
    long PositionMs = 0,
    long DurationMs = 0,
    bool IsPlaying = false,
    string? Error = null,
    IReadOnlyList<PlaybackSubtitleTrack>? SubtitleTracks = null,
    int? SelectedSubtitleTrackId = null);

public sealed class WebControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly int _port;
    private readonly WebControlTokenStore _tokens;
    private readonly Func<IReadOnlyList<LibrarySeries>> _getSeries;
    private readonly Func<PlaybackRuntimeStatus> _getPlaybackStatus;
    private readonly Func<string, long?, Task<bool>> _playEpisode;
    private readonly Func<PlaybackControlCommand, Task<PlaybackRuntimeStatus>> _playbackCommand;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<Func<AppSettings, AppSettings>, Task<AppSettings>> _updateSettings;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly MediaSourceActions _mediaSources;
    private readonly BangumiMetadataClient _bangumiMetadata;
    private readonly TmdbMetadataClient _tmdbMetadata;
    private readonly MetadataTokenStore _metadataTokens;
    private readonly RssSubscriptionStore _rssSubscriptions;
    private readonly CloudDriveAutomationStore _cloudDriveConfig;
    private readonly CloudDriveCredentialStore _cloudDriveCredentials;
    private readonly CloudDriveGrpcClient _cloudDriveClient;
    private readonly RssFeedClient _rssFeedClient;
    private readonly RssProcessedStore _rssProcessed;
    private readonly CloudDriveRssRunner _cloudDriveRunner;
    private readonly Func<LibrarySeries, CancellationToken, Task<string?>> _resolvePosterPath;
    private readonly bool _listenOnAnyIp;
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private WebApplication? _app;

    public WebControlServer(
        int port,
        WebControlTokenStore tokens,
        Func<IReadOnlyList<LibrarySeries>> getSeries,
        Func<PlaybackRuntimeStatus> getPlaybackStatus,
        Func<string, long?, Task<bool>> playEpisode,
        Func<PlaybackControlCommand, Task<PlaybackRuntimeStatus>> playbackCommand,
        Func<AppSettings> getSettings,
        Func<Func<AppSettings, AppSettings>, Task<AppSettings>> updateSettings,
        MediaSourceActions mediaSources,
        bool listenOnAnyIp = true,
        BangumiMetadataClient? bangumiMetadata = null,
        TmdbMetadataClient? tmdbMetadata = null,
        MetadataTokenStore? metadataTokens = null,
        RssSubscriptionStore? rssSubscriptions = null,
        CloudDriveAutomationStore? cloudDriveConfig = null,
        CloudDriveCredentialStore? cloudDriveCredentials = null,
        CloudDriveGrpcClient? cloudDriveClient = null,
        RssFeedClient? rssFeedClient = null,
        RssProcessedStore? rssProcessed = null,
        CloudDriveRssRunner? cloudDriveRunner = null,
        Func<LibrarySeries, CancellationToken, Task<string?>>? resolvePosterPath = null)
    {
        _port = port;
        _tokens = tokens;
        _getSeries = getSeries;
        _getPlaybackStatus = getPlaybackStatus;
        _playEpisode = playEpisode;
        _playbackCommand = playbackCommand;
        _getSettings = getSettings;
        _updateSettings = updateSettings;
        _mediaSources = mediaSources;
        _bangumiMetadata = bangumiMetadata ?? new BangumiMetadataClient();
        _tmdbMetadata = tmdbMetadata ?? new TmdbMetadataClient();
        _metadataTokens = metadataTokens ?? new MetadataTokenStore();
        _rssSubscriptions = rssSubscriptions ?? new RssSubscriptionStore();
        _cloudDriveConfig = cloudDriveConfig ?? new CloudDriveAutomationStore();
        _cloudDriveCredentials = cloudDriveCredentials ?? new CloudDriveCredentialStore();
        _cloudDriveClient = cloudDriveClient ?? new CloudDriveGrpcClient();
        _rssFeedClient = rssFeedClient ?? new RssFeedClient();
        _rssProcessed = rssProcessed ?? new RssProcessedStore();
        _cloudDriveRunner = cloudDriveRunner ?? new CloudDriveRssRunner(
            _cloudDriveConfig,
            _cloudDriveCredentials,
            _rssSubscriptions,
            _rssFeedClient,
            _rssProcessed,
            _cloudDriveClient);
        _resolvePosterPath = resolvePosterPath ?? ((series, _) => Task.FromResult(
            series.PosterUri?.IsFile == true ? series.PosterPath : null));
        _listenOnAnyIp = listenOnAnyIp;
    }

    public IReadOnlyList<string> Urls => _listenOnAnyIp
        ? LocalAddresses(_port)
        : [$"http://{IPAddress.Loopback}:{_port}"];

    public string PreferredAccessUrl => _listenOnAnyIp && Urls.Count > 1 ? Urls[1] : Urls[0];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (_listenOnAnyIp) options.ListenAnyIP(_port);
            else options.ListenLocalhost(_port);
        });
        var app = builder.Build();
        ConfigureMiddleware(app);
        ConfigureRoutes(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    private void ConfigureMiddleware(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api") && !IsAuthorized(context.Request))
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status401Unauthorized, null, "未授权").ConfigureAwait(false);
                return;
            }

            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (BadHttpRequestException error)
            {
                await WriteEnvelopeAsync(context, error.StatusCode, null, error.Message).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status400BadRequest, null, error.Message).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status502BadGateway, null, error.Message).ConfigureAwait(false);
            }
            catch (KeyNotFoundException error)
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status404NotFound, null, error.Message).ConfigureAwait(false);
            }
            catch (NotSupportedException error)
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status501NotImplemented, null, error.Message).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException or TimeoutException)
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status409Conflict, null, error.Message).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await WriteEnvelopeAsync(context, StatusCodes.Status500InternalServerError, null, "服务器内部错误").ConfigureAwait(false);
            }
        });
    }

    private void ConfigureRoutes(WebApplication app)
    {
        app.MapGet("/api/info", () => Success(new
        {
            appName = "MiruPlay",
            deviceName = Environment.MachineName,
            port = _port,
            localIps = LocalIpAddresses(),
            startedAt = _startedAt,
            versionName = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "",
            versionCode = 0,
            packageName = "MiruPlay.Windows",
        }));

        app.MapGet("/api/sources", () => Success(_mediaSources.List()));
        app.MapGet("/api/local-directories", (HttpRequest request) => Success(BrowseLocalDirectories(request.Query["path"].ToString())));
        app.MapPost("/api/sources/test", async (HttpRequest request) =>
        {
            var body = await ReadSourceRequestAsync(request).ConfigureAwait(false);
            return Success(await _mediaSources.Test(body).ConfigureAwait(false));
        });
        app.MapPost("/api/sources", async (HttpRequest request) =>
        {
            var body = await ReadSourceRequestAsync(request).ConfigureAwait(false);
            return Success(await _mediaSources.Add(body).ConfigureAwait(false));
        });
        app.MapPut("/api/sources/{id}", async (string id, HttpRequest request) =>
        {
            if (!long.TryParse(id, out var sourceId)) throw new BadHttpRequestException("媒体源 ID 不正确");
            var body = await ReadSourceRequestAsync(request).ConfigureAwait(false);
            return Success(await _mediaSources.Update(sourceId, body).ConfigureAwait(false));
        });
        app.MapDelete("/api/sources/{id}", async (string id) =>
        {
            if (!long.TryParse(id, out var sourceId)) throw new BadHttpRequestException("媒体源 ID 不正确");
            await _mediaSources.Remove(sourceId).ConfigureAwait(false);
            return Success(new { });
        });
        app.MapPost("/api/sources/{id}/scan", async (string id) =>
        {
            if (!long.TryParse(id, out var sourceId)) throw new BadHttpRequestException("媒体源 ID 不正确");
            return Success(await _mediaSources.Scan(sourceId).ConfigureAwait(false));
        });

        app.MapGet("/api/settings/scan", () => Success(ToScanSettings(_getSettings(), _mediaSources.List())));
        app.MapPut("/api/settings/scan", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<ScanSettingsRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? new ScanSettingsRequest();
            if (body.AutoScanEnabled == true || body.MergeSameAnimeEnabled == true ||
                body.AutoScanIntervalHours is not (null or 24) ||
                body.PosterWallArrangement is not (null or "TITLE"))
            {
                throw new NotSupportedException("Windows 客户端当前仅支持手动扫描和标题排列。");
            }
            var requestedMode = body.CurrentAppMode;
            if (requestedMode is not (null or "anime" or "drama")) throw new BadHttpRequestException("currentAppMode 不正确");
            var updated = await UpdateSettingsAsync(current => current with
            {
                CurrentAppMode = requestedMode ?? current.CurrentAppMode,
            }).ConfigureAwait(false);
            return Success(ToScanSettings(updated, _mediaSources.List()));
        });

        app.MapGet("/api/cloud-drive", () => Success(GetCloudDriveAutomationDto()));
        app.MapGet("/api/cloud-drive/directories", async (HttpRequest request) =>
        {
            var configuredEndpoint = _cloudDriveConfig.Load().EndpointUrl;
            var endpoint = request.Query["endpointUrl"].ToString().Trim();
            if (endpoint.Length == 0) endpoint = configuredEndpoint;
            var token = _cloudDriveCredentials.LoadForEndpoint(endpoint).Token;
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("请先登录或验证 CloudDrive2 API Token。");
            var tokenInfo = await _cloudDriveClient.GetApiTokenInfoAsync(endpoint, token, request.HttpContext.RequestAborted).ConfigureAwait(false);
            var root = NormalizeCloudDrivePath(tokenInfo.RootDir);
            var requested = NormalizeCloudDrivePath(request.Query["path"].ToString());
            var path = IsWithinCloudDriveRoot(requested, root) ? requested : root;
            var files = await _cloudDriveClient.ListFolderAsync(endpoint, token, path, cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
            var entries = files
                .Where(file => file.IsDirectory && TryNormalizeCloudDrivePath(file.Path, out _))
                .Select(file => new { File = file, Path = NormalizeCloudDrivePath(file.Path) })
                .Where(item => IsWithinCloudDriveRoot(item.Path, root))
                .Select(item => new
                {
                    name = string.IsNullOrWhiteSpace(item.File.Name) ? item.Path[(item.Path.LastIndexOf('/') + 1)..] : item.File.Name.Trim(),
                    path = item.Path,
                    canRead = true,
                })
                .Where(item => item.name.Length > 0 && !item.name.StartsWith('.'))
                .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Success(new
            {
                path,
                displayPath = path == "/" ? "CloudDrive 根目录" : path,
                parentPath = CloudDriveParentPath(path, root),
                entries,
            });
        });
        app.MapPut("/api/cloud-drive/config", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CloudDriveConfigRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            var current = _cloudDriveConfig.Load();
            _cloudDriveConfig.Save(new CloudDriveAutomationConfig(
                body.EndpointUrl,
                body.Username,
                body.WebDavSourceId,
                body.InboxPath,
                body.LibraryPath,
                body.LibraryMode,
                body.IntervalMinutes,
                body.Enabled,
                current.LastRunAt,
                body.RssProxyEnabled,
                body.RssProxyHost,
                body.RssProxyPort));
            return Success(GetCloudDriveAutomationDto());
        });
        app.MapPost("/api/cloud-drive/login", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CloudDriveLoginRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            var login = await _cloudDriveClient.LoginAsync(
                body.EndpointUrl,
                body.Username,
                body.Password,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            _ = await _cloudDriveClient.GetApiTokenInfoAsync(
                body.EndpointUrl,
                login.Token,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            _cloudDriveCredentials.SavePassword(body.EndpointUrl, body.Password);
            _cloudDriveCredentials.SaveToken(body.EndpointUrl, login.Token);
            return Success(GetCloudDriveAutomationDto());
        });
        app.MapPost("/api/cloud-drive/token", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CloudDriveTokenRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            var info = await _cloudDriveClient.GetApiTokenInfoAsync(
                body.EndpointUrl,
                body.Token,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            _cloudDriveCredentials.SaveToken(body.EndpointUrl, body.Token);
            return Success(info);
        });
        app.MapDelete("/api/settings/cloud-drive/credentials", () =>
        {
            _cloudDriveCredentials.Clear();
            return Success(new { tokenConfigured = false, passwordConfigured = false });
        });
        app.MapGet("/api/cloud-drive/run", () => Success(_cloudDriveRunner.Status));
        app.MapPost("/api/cloud-drive/run", async (HttpRequest request) =>
            Success(await _cloudDriveRunner.RunAsync(request.HttpContext.RequestAborted).ConfigureAwait(false)));

        app.MapPost("/api/cloud-drive/offline", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CloudDriveOfflineRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            var config = _cloudDriveConfig.Load();
            var token = _cloudDriveCredentials.LoadForEndpoint(config.EndpointUrl).Token;
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("请先登录或验证 CloudDrive2 API Token。");
            var tokenInfo = await _cloudDriveClient.GetApiTokenInfoAsync(config.EndpointUrl, token, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!tokenInfo.AllowAddOfflineDownload) throw new InvalidOperationException("CloudDrive2 API Token 没有离线下载权限。");
            var root = NormalizeCloudDrivePath(tokenInfo.RootDir);
            var target = NormalizeCloudDrivePath(body.TargetFolder);
            if (!IsWithinCloudDriveRoot(target, root)) throw new ArgumentException("离线下载目录超出 CloudDrive2 Token 根目录。");
            await _cloudDriveClient.AddOfflineFilesAsync(
                config.EndpointUrl,
                token,
                body.Urls,
                target,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Success(new { submitted = body.Urls.Count, targetFolder = target });
        });

        app.MapPost("/api/cloud-drive/rss/{id:long}/preview", async (long id, HttpRequest request) =>
        {
            var subscription = _rssSubscriptions.List().FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("RSS 订阅不存在。");
            var config = _cloudDriveConfig.Load();
            var items = await _rssFeedClient.FetchAsync(
                subscription.Url,
                config.RssProxyEnabled,
                config.RssProxyHost,
                config.RssProxyPort,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            var decisions = RssSubmissionPlanner.Plan(items, subscription.FilterRegex);
            var preview = decisions.Take(200).Select(decision => new
            {
                title = decision.Item.Title,
                submissionUrl = decision.SubmissionUrl,
                itemKey = decision.ItemKey,
                status = decision.Status switch
                {
                    RssSubmissionStatus.WouldSubmit => "WOULD_SUBMIT",
                    RssSubmissionStatus.SkippedFilter => "SKIPPED_FILTER",
                    _ => "MISSING_SUBMISSION",
                },
                processed = decision.ItemKey is not null && _rssProcessed.IsProcessed(id, decision.ItemKey),
            }).ToList();
            return Success(new
            {
                subscriptionId = id,
                total = decisions.Count,
                wouldSubmit = decisions.Count(decision => decision.Status == RssSubmissionStatus.WouldSubmit),
                skipped = decisions.Count(decision => decision.Status == RssSubmissionStatus.SkippedFilter),
                missing = decisions.Count(decision => decision.Status == RssSubmissionStatus.MissingSubmission),
                truncated = decisions.Count > preview.Count,
                items = preview,
            });
        });

        app.MapPost("/api/cloud-drive/rss", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<RssSubscriptionRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            return Success(_rssSubscriptions.Add(body));
        });
        app.MapPut("/api/cloud-drive/rss/{id:long}", async (long id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<RssSubscriptionRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            return Success(_rssSubscriptions.Update(id, body));
        });
        app.MapDelete("/api/cloud-drive/rss/{id:long}", (long id) =>
        {
            _rssSubscriptions.Remove(id);
            return Success(new { });
        });

        app.MapGet("/api/library", (HttpRequest request) =>
        {
            var query = request.Query["query"].ToString().Trim();
            var series = _getSeries();
            if (query.Length > 0)
            {
                series = series.Where(item =>
                    item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    (item.OriginalTitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                    item.ApiId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return Success(ToLibraryDto(series));
        });

        app.MapGet("/api/metadata/bangumi/search", async (HttpRequest request) =>
        {
            var query = request.Query["query"].ToString();
            var limitText = request.Query["limit"].ToString();
            var limit = limitText.Length == 0
                ? 10
                : int.TryParse(limitText, out var parsed)
                    ? parsed
                    : throw new BadHttpRequestException("limit 必须是整数。");
            return Success(await _bangumiMetadata.SearchAsync(
                query,
                limit,
                request.HttpContext.RequestAborted).ConfigureAwait(false));
        });

        app.MapGet("/api/metadata/bangumi/me", async (HttpRequest request) =>
        {
            var token = _metadataTokens.Load().Bangumi ?? string.Empty;
            return Success(await _bangumiMetadata.GetCurrentUserAsync(
                token,
                request.HttpContext.RequestAborted).ConfigureAwait(false));
        });

        app.MapGet("/api/metadata/bangumi/subjects/{id:int}/collection", async (int id, HttpRequest request) =>
        {
            var token = _metadataTokens.Load().Bangumi ?? string.Empty;
            var collection = await _bangumiMetadata.GetSubjectCollectionAsync(
                id,
                token,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Success(new { collected = collection is not null, collection });
        });

        app.MapPut("/api/metadata/bangumi/subjects/{id:int}/collection", async (int id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<BangumiCollectionRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            var token = _metadataTokens.Load().Bangumi ?? string.Empty;
            await _bangumiMetadata.UpsertSubjectCollectionAsync(
                id,
                body.Type,
                token,
                request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Success(new { subjectId = id, type = body.Type });
        });

        app.MapPut("/api/metadata/bangumi/episodes/{id:int}/collection", async (int id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<BangumiCollectionRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            var token = _metadataTokens.Load().Bangumi ?? string.Empty;
            await _bangumiMetadata.UpdateEpisodeCollectionAsync(id, body.Type, token, request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Success(new { episodeId = id, type = body.Type });
        });

        app.MapGet("/api/metadata/bangumi/subjects/{id:int}/episodes", async (int id, HttpRequest request) =>
        {
            var token = _metadataTokens.Load().Bangumi ?? string.Empty;
            return Success(await _bangumiMetadata.GetEpisodeCollectionsAsync(id, token, request.HttpContext.RequestAborted).ConfigureAwait(false));
        });

        app.MapGet("/api/metadata/tmdb/search", async (HttpRequest request) =>
        {
            var query = request.Query["query"].ToString();
            var limit = ParseOptionalInt(request.Query["limit"].ToString(), "limit") ?? 10;
            var year = ParseOptionalInt(request.Query["year"].ToString(), "year");
            var token = _metadataTokens.Load().Tmdb ?? string.Empty;
            return Success(await _tmdbMetadata.SearchAsync(
                query,
                token,
                year,
                limit,
                request.HttpContext.RequestAborted).ConfigureAwait(false));
        });

        app.MapGet("/api/settings/metadata", () =>
        {
            var tokens = _metadataTokens.Load();
            return Success(new
            {
                bangumiTokenConfigured = !string.IsNullOrEmpty(tokens.Bangumi),
                tmdbTokenConfigured = !string.IsNullOrEmpty(tokens.Tmdb),
            });
        });
        app.MapPut("/api/settings/metadata/bangumi-token", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<MetadataTokenRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            _metadataTokens.SaveBangumi(body.Token);
            return Success(new { bangumiTokenConfigured = true });
        });
        app.MapDelete("/api/settings/metadata/bangumi-token", () =>
        {
            _metadataTokens.ClearBangumi();
            return Success(new { bangumiTokenConfigured = false });
        });
        app.MapPut("/api/settings/metadata/tmdb-token", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<MetadataTokenRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空。");
            _metadataTokens.SaveTmdb(body.Token);
            return Success(new { tmdbTokenConfigured = true });
        });
        app.MapDelete("/api/settings/metadata/tmdb-token", () =>
        {
            _metadataTokens.ClearTmdb();
            return Success(new { tmdbTokenConfigured = false });
        });

        app.MapGet("/api/anime/{id}/poster", async (string id, HttpRequest request) =>
        {
            var series = _getSeries().FirstOrDefault(item => item.ApiId == id)
                ?? throw new BadHttpRequestException("番剧不存在", StatusCodes.Status404NotFound);
            var path = await _resolvePosterPath(series, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (path is not null && File.Exists(path)) return Results.File(path, PosterContentType(path));
            if (series.PosterUri?.Scheme is "http" or "https") return Results.Redirect(series.PosterPath!);
            throw new BadHttpRequestException("海报不存在", StatusCodes.Status404NotFound);
        });

        app.MapGet("/api/anime/{id}", (string id) =>
        {
            var series = _getSeries().FirstOrDefault(item => item.ApiId == id)
                ?? throw new BadHttpRequestException("番剧不存在", StatusCodes.Status404NotFound);
            return Success(new
            {
                anime = ToAnimeDto(series),
                episodes = series.Episodes.Select(episode => new
                {
                    episode = ToEpisodeDto(series, episode),
                    progressMs = episode.WatchedPositionMs,
                    lastWatched = episode.LastWatchedEpochMs,
                    playCount = episode.PlayCount,
                }),
            });
        });

        app.MapPost("/api/playback/play", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<PlayEpisodeRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空");
            if (!await _playEpisode(body.EpisodeId, body.StartPositionMs).ConfigureAwait(false))
            {
                throw new BadHttpRequestException("剧集不存在", StatusCodes.Status404NotFound);
            }
            return Success(_getPlaybackStatus());
        });

        app.MapGet("/api/playback/status", () => Success(_getPlaybackStatus()));

        app.MapPost("/api/playback/command", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<PlaybackCommandRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("请求不能为空");
            if (string.IsNullOrWhiteSpace(body.Command)) throw new BadHttpRequestException("command 不能为空");
            var status = await _playbackCommand(new PlaybackControlCommand(
                body.Command,
                body.PositionMs,
                body.DeltaMs,
                body.Speed,
                body.SubtitleTrackId)).ConfigureAwait(false);
            return Success(status);
        });

        app.MapGet("/api/settings/playback", () => Success(ToPlaybackSettings(_getSettings())));
        app.MapPut("/api/settings/playback", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<PlaybackSettingsRequest>(request.Body, JsonOptions).ConfigureAwait(false)
                ?? new PlaybackSettingsRequest();
            if (body.EndAction is not (null or "return_to_detail" or "play_next_episode")) throw new BadHttpRequestException("endAction 不正确");
            if (body.PreferredSubtitleLanguage is not (null or "auto" or "zh_hans" or "zh_hant" or "zh" or "en" or "ja")) throw new BadHttpRequestException("preferredSubtitleLanguage 不正确");
            if (body.FormatAwareToneMapping is not null)
            {
                throw new BadHttpRequestException("Windows 客户端不支持 Android 色调映射设置。", StatusCodes.Status501NotImplemented);
            }
            var updated = await UpdateSettingsAsync(current => current with
            {
                PlaybackEndAction = body.EndAction ?? current.PlaybackEndAction,
                PreferredSubtitleLanguage = body.PreferredSubtitleLanguage ?? current.PreferredSubtitleLanguage,
            }).ConfigureAwait(false);
            return Success(ToPlaybackSettings(updated));
        });

        app.MapGet("/api/web-control/access", () => Success(new
        {
            enabled = _getSettings().WebControlEnabled,
            accessToken = _tokens.AccessToken,
            urls = Urls,
        }));
        app.MapPost("/api/web-control/access/rotate-token", () =>
        {
            _tokens.Rotate();
            return Success(new
            {
                enabled = _getSettings().WebControlEnabled,
                accessToken = _tokens.AccessToken,
                urls = Urls,
            });
        });

        foreach (var (method, path) in new (string Method, string Path)[]
        {
            ("GET", "/api/app-update"),
            ("POST", "/api/app-update/check"),
            ("POST", "/api/app-update/download"),
            ("POST", "/api/app-update/install-permission"),
            ("GET", "/api/playback/clock-samples"),
            ("GET", "/api/playback/native-diagnostics"),
            ("POST", "/api/playback/native-profile"),
            ("GET", "/api/playback/native-profile/download"),
            ("POST", "/api/playback/profile"),
            ("GET", "/api/playback/debug-config"),
            ("PUT", "/api/playback/debug-config"),
        })
        {
            app.MapMethods(path, [method], () => UnsupportedOnWindows());
        }

        app.MapFallback("/api/{**path}", () =>
            Results.Json(Failure("接口不存在"), JsonOptions, statusCode: StatusCodes.Status404NotFound));
        app.MapGet("/", () => EmbeddedWebResource("index.html", "text/html; charset=utf-8"));
        app.MapGet("/web/app.css", () => EmbeddedWebResource("app.css", "text/css; charset=utf-8"));
        app.MapGet("/web/app.js", () => EmbeddedWebResource("app.js", "text/javascript; charset=utf-8"));
    }

    private static IResult EmbeddedWebResource(string fileName, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MiruPlay.Windows.Web.{fileName}")
            ?? throw new InvalidOperationException($"缺少 WebUI 资源: {fileName}");
        return Results.Stream(stream, contentType);
    }

    private static string PosterContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    private static async Task<MediaSourceRequest> ReadSourceRequestAsync(HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<MediaSourceRequest>(request.Body, JsonOptions).ConfigureAwait(false)
            ?? throw new BadHttpRequestException("请求不能为空");
        if (string.IsNullOrWhiteSpace(body.Name)) throw new BadHttpRequestException("name 不能为空");
        if (string.IsNullOrWhiteSpace(body.Type)) throw new BadHttpRequestException("type 不能为空");
        if (string.IsNullOrWhiteSpace(body.Location)) throw new BadHttpRequestException("location 不能为空");
        return body;
    }

    private static object BrowseLocalDirectories(string requestedPath)
    {
        var path = requestedPath.Trim();
        if (path.Length == 0)
        {
            var drives = Directory.GetLogicalDrives()
                .Select(drive => new { name = drive, path = drive, canRead = CanReadDirectory(drive) })
                .ToList();
            return new { path = "", displayPath = "此电脑", parentPath = (string?)null, entries = drives };
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new InvalidDataException("目录不存在。");
        try
        {
            var entries = Directory.EnumerateDirectories(fullPath)
                .Select(directory => new
                {
                    name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    path = directory,
                    canRead = true,
                })
                .OrderBy(entry => entry.name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new
            {
                path = fullPath,
                displayPath = fullPath,
                parentPath = Directory.GetParent(fullPath)?.FullName,
                entries,
            };
        }
        catch (UnauthorizedAccessException error)
        {
            throw new InvalidDataException("无权读取该目录。", error);
        }
    }

    private static bool CanReadDirectory(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var token = request.Headers["X-MiruPlay-Token"].FirstOrDefault();
        if (_tokens.Matches(token)) return true;

        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true &&
            _tokens.Matches(authorization[7..].Trim())) return true;

        if (_tokens.Matches(request.Query["token"].FirstOrDefault())) return true;
        return request.Cookies.TryGetValue("miruplay_web_token", out var cookie) && _tokens.Matches(cookie);
    }

    private static object ToLibraryDto(IReadOnlyList<LibrarySeries> series)
    {
        var continueWatching = series
            .SelectMany(item => item.Episodes
                .Where(episode => episode.IsInProgress)
                .Select(episode => new
                {
                    progressEpisodeId = episode.ApiId,
                    positionMs = episode.WatchedPositionMs,
                    lastWatched = episode.LastWatchedEpochMs,
                    playCount = episode.PlayCount,
                    episode = ToEpisodeDto(item, episode),
                    anime = ToAnimeDto(item),
                }))
            .OrderByDescending(item => item.lastWatched)
            .Take(30)
            .ToList();
        var anime = series.Select(ToAnimeDto).ToList();
        return new { continueWatching, recentlyAdded = anime.Take(30).ToList(), allAnime = anime };
    }

    private static object ToAnimeDto(LibrarySeries series) => new
    {
        id = series.ApiId,
        title = series.OriginalTitle ?? series.Title,
        titleCn = series.OriginalTitle is null ? null : series.Title,
        summary = series.Summary,
        genres = series.Genres,
        studio = (string?)null,
        director = (string?)null,
        episodeCount = series.Episodes.Count,
        airDate = series.AirDate ?? series.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        rating = 0,
        bangumiId = series.ExternalId("Bangumi")?.NumericValue,
        anilistId = (int?)null,
        tmdbId = series.ExternalId("TMDB")?.NumericValue,
        externalIds = series.ExternalIds.Select(item => new { provider = item.Provider, value = item.Value }).ToList(),
        metadataLinks = series.ExternalIds.Where(item => item.Link is not null).Select(item => new
        {
            provider = item.Provider,
            url = item.Link!.AbsoluteUri,
        }).ToList(),
        posterUrl = series.PosterUri is null
            ? null
            : $"/api/anime/{Uri.EscapeDataString(series.ApiId)}/poster",
        posterLocalPath = (string?)null,
        fanartUrl = (string?)null,
        bangumiCollectionType = (int?)null,
        bangumiEpStatus = 0,
    };

    private static object ToEpisodeDto(LibrarySeries series, LibraryEpisode episode) => new
    {
        id = episode.ApiId,
        animeId = series.ApiId,
        seasonNumber = episode.Season,
        episodeNumber = Convert.ToInt32(episode.Number),
        title = episode.Title,
        filePath = episode.MediaPath,
        fileName = Path.GetFileName(episode.MediaPath),
        duration = episode.Duration > TimeSpan.Zero ? Convert.ToInt64(episode.Duration.TotalMilliseconds) : episode.WatchedDurationMs,
        watchedPosition = episode.WatchedPositionMs,
        lastWatchedTimestamp = episode.LastWatchedEpochMs,
        playCount = episode.PlayCount,
        thumbnailPath = (string?)null,
        bangumiEpisodeId = episode.ExternalId("Bangumi")?.NumericValue,
        externalIds = episode.ExternalIds.Select(item => new { provider = item.Provider, value = item.Value }).ToList(),
        bangumiCollectionType = (int?)null,
    };

    private static int? ParseOptionalInt(string value, string name)
    {
        if (value.Length == 0) return null;
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new BadHttpRequestException($"{name} 必须是整数。");
    }

    private sealed record MetadataTokenRequest(string Token);
    private sealed record BangumiCollectionRequest(int Type);
    private sealed record CloudDriveLoginRequest(string EndpointUrl, string Username, string Password);
    private sealed record CloudDriveTokenRequest(string EndpointUrl, string Token);
    private sealed record CloudDriveOfflineRequest(IReadOnlyList<string> Urls, string TargetFolder);

    private static object ToScanSettings(AppSettings settings, IReadOnlyList<MediaSourceInfoDto> sources) => new
    {
        autoScanEnabled = false,
        autoScanIntervalHours = 24,
        lastScanAt = sources.Count == 0 ? 0 : sources.Max(source => source.LastScanned),
        mergeSameAnimeEnabled = false,
        posterWallArrangement = "TITLE",
        currentAppMode = settings.CurrentAppMode,
        appModeOptions = new[] { "anime", "drama" },
        posterWallArrangementOptions = new[] { "TITLE" },
        autoScanIntervalOptionsHours = new[] { 24 },
    };

    private static object ToPlaybackSettings(AppSettings settings) => new
    {
        endAction = settings.PlaybackEndAction,
        preferredSubtitleLanguage = settings.PreferredSubtitleLanguage,
        formatAwareToneMapping = new { defaultBackend = "STANDARD_EXO", rules = new Dictionary<string, object>() },
        endActionOptions = new[] { "return_to_detail", "play_next_episode" },
        preferredSubtitleLanguageOptions = new[] { "auto", "zh_hans", "zh_hant", "zh", "en", "ja" },
    };

    private static string NormalizeCloudDrivePath(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) throw new ArgumentException("CloudDrive2 目录不能包含路径遍历。", nameof(path));
        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private static bool TryNormalizeCloudDrivePath(string path, out string normalized)
    {
        try
        {
            normalized = NormalizeCloudDrivePath(path);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static bool IsWithinCloudDriveRoot(string path, string root) =>
        root == "/" || path == root || path.StartsWith($"{root}/", StringComparison.Ordinal);

    private static string? CloudDriveParentPath(string path, string root)
    {
        if (path == "/" || path == root) return null;
        var separator = path.LastIndexOf('/');
        var parent = separator <= 0 ? "/" : path[..separator];
        return IsWithinCloudDriveRoot(parent, root) ? parent : root;
    }

    private object GetCloudDriveAutomationDto()
    {
        var config = _cloudDriveConfig.Load();
        var credentials = _cloudDriveCredentials.Load();
        var normalizedEndpoint = CloudDriveGrpcClient.ValidateEndpoint(config.EndpointUrl).AbsoluteUri.TrimEnd('/');
        var credentialsMatchEndpoint = string.Equals(
            credentials.EndpointUrl,
            normalizedEndpoint,
            StringComparison.OrdinalIgnoreCase);
        return new
        {
            config,
            subscriptions = _rssSubscriptions.List(),
            tokenConfigured = credentialsMatchEndpoint && !string.IsNullOrEmpty(credentials.Token),
            passwordConfigured = credentialsMatchEndpoint && !string.IsNullOrEmpty(credentials.Password),
        };
    }

    private async Task<AppSettings> UpdateSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        await _settingsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _updateSettings(update).ConfigureAwait(false);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private static IResult Success(object data) => Results.Json(new ApiEnvelope<object>(true, data, null), JsonOptions);
    private static IResult UnsupportedOnWindows() => Results.Json(
        Failure("此操作仅适用于 Android，Windows 客户端不支持。"),
        JsonOptions,
        statusCode: StatusCodes.Status501NotImplemented);
    private static ApiEnvelope<object> Failure(string error) => new(false, null, error);

    private static async Task WriteEnvelopeAsync(HttpContext context, int statusCode, object? data, string? error)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, new ApiEnvelope<object>(false, data, error), JsonOptions).ConfigureAwait(false);
    }

    private static List<string> LocalAddresses(int port) =>
        new[] { IPAddress.Loopback.ToString() }
            .Concat(LocalIpAddresses())
            .Distinct(StringComparer.Ordinal)
            .Select(ip => $"http://{ip}:{port}")
            .ToList();

    private static List<string> LocalIpAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
        .Select(address => address.Address.ToString())
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private sealed record ApiEnvelope<T>(bool Ok, T? Data, string? Error);
    private sealed record PlayEpisodeRequest(string EpisodeId, long? StartPositionMs = null);
    private sealed record PlaybackCommandRequest(
        string? Command,
        long? PositionMs = null,
        long? DeltaMs = null,
        float? Speed = null,
        int? SubtitleTrackId = null);
    private sealed record PlaybackSettingsRequest(
        string? EndAction = null,
        string? PreferredSubtitleLanguage = null,
        JsonElement? FormatAwareToneMapping = null);
    private sealed record ScanSettingsRequest(
        bool? AutoScanEnabled = null,
        int? AutoScanIntervalHours = null,
        bool? MergeSameAnimeEnabled = null,
        string? PosterWallArrangement = null,
        string? CurrentAppMode = null);
}
