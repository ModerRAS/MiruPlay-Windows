using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class WebControlServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-web-{Guid.NewGuid():N}");

    public WebControlServerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ApiEnforcesTokensAndServesLibraryPlaybackAndSettings()
    {
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "token.bin"));
        var episode = Episode();
        var posterPath = Path.Combine(_directory, "poster.png");
        File.WriteAllBytes(posterPath, [0x89, 0x50, 0x4E, 0x47]);
        var series = Series(episode) with { PosterPath = "https://webdav.test/private/poster.png" };
        var settings = new AppSettings(WebControlPort: port);
        Action? beforeSettingsUpdate = null;
        string? requestedEpisode = null;
        long? requestedPosition = null;
        PlaybackControlCommand? requestedCommand = null;
        MediaSourceRequest? requestedSource = null;
        var sourceInfo = new MediaSourceInfoDto(
            1,
            "Anime",
            "LOCAL",
            "ANIME",
            new Dictionary<string, string> { ["path"] = _directory },
            true,
            1);
        var sourceActions = new MediaSourceActions(
            () => [sourceInfo],
            _ => Task.FromResult(new SourceTestResponse(true, "ok")),
            request =>
            {
                requestedSource = request;
                return Task.FromResult(sourceInfo);
            },
            (_, request) =>
            {
                requestedSource = request;
                return Task.FromResult(sourceInfo);
            },
            _ => Task.CompletedTask,
            _ => Task.FromResult(new SourceScanResponse(1, "Anime", 1, 0, 0)));
        using var bangumiMetadata = new BangumiMetadataClient(
            new StaticJsonHandler("""
                { "data": [{ "id": 431767, "type": 2, "name": "Sousou no Frieren", "name_cn": "葬送的芙莉莲" }] }
                """),
            new Uri("https://bangumi.test/v0/"));
        using var tmdbMetadata = new TmdbMetadataClient(
            new StaticJsonHandler("""
                { "results": [{ "id": 209867, "name": "Frieren", "original_name": "Sousou no Frieren" }] }
                """),
            new Uri("https://tmdb.test/3/"));
        var metadataTokens = new MetadataTokenStore(Path.Combine(_directory, "metadata-tokens.bin"));
        var rssSubscriptions = new RssSubscriptionStore(Path.Combine(_directory, "rss-subscriptions.json"));
        var cloudDriveConfig = new CloudDriveAutomationStore(Path.Combine(_directory, "cloud-drive.json"));
        var cloudDriveCredentials = new CloudDriveCredentialStore(Path.Combine(_directory, "cloud-drive-credentials.bin"));
        var rssFeedClient = new RssFeedClient(new StaticJsonHandler("""
            <rss version="2.0"><channel>
              <item><title>Frieren 01</title><guid>rss-1</guid><link>magnet:?xt=urn:btih:abc</link></item>
              <item><title>Other 02</title><guid>rss-2</guid><link>magnet:?xt=urn:btih:def</link></item>
              <item><title>Frieren 03</title><guid>rss-3</guid></item>
            </channel></rss>
            """));
        var rssProcessed = new RssProcessedStore(Path.Combine(_directory, "rss-state.db"));
        var localLogs = new RotatingLocalLogStore(Path.Combine(_directory, "logs", "miruplay.jsonl"));
        localLogs.Write("info", "test log");
        var openObserveLogs = new OpenObserveLogService(
            localLogs,
            new OpenObserveTokenStore(Path.Combine(_directory, "openobserve-token.bin")));
        var cloudDriveClient = new CloudDriveGrpcClient(
            (_, username, password, _) =>
            {
                Assert.Equal("cloud-user", username);
                Assert.Equal("cloud-password-secret", password);
                return Task.FromResult(new CloudDriveLoginResult("issued-cloud-token"));
            },
            (_, token, _) =>
            {
                Assert.True(token is "manual-cloud-token" or "issued-cloud-token");
                return Task.FromResult(new CloudDriveTokenInfo("/Anime", "MiruPlay", true, true, false, false, true, true));
            },
            (_, token, path, forceRefresh, _) =>
            {
                Assert.Equal("manual-cloud-token", token);
                Assert.Equal("/Anime", path);
                Assert.False(forceRefresh);
                return Task.FromResult<IReadOnlyList<CloudDriveFileInfo>>([
                    new("Season 2", "/Anime/Season 2", true, 0),
                    new("episode.mkv", "/Anime/episode.mkv", false, 100),
                    new(".hidden", "/Anime/.hidden", true, 0),
                    new("Outside", "/Outside", true, 0),
                    new("Season 1", "/Anime/Season 1", true, 0),
                ]);
            },
            (_, token, urls, target, _) =>
            {
                Assert.Equal("manual-cloud-token", token);
                Assert.True(
                    urls.SequenceEqual(["magnet:?xt=urn:btih:abc", "https://example.test/file.torrent"]) ||
                    urls.SequenceEqual(["magnet:?xt=urn:btih:abc"]));
                Assert.Equal("/Anime/Downloads", target);
                return Task.CompletedTask;
            });
        await using var server = new WebControlServer(
            port,
            tokens,
            () => [series],
            () => new PlaybackRuntimeStatus(
                State: "PLAYING",
                Title: "第 1 集 · 测试分集",
                SubtitleTracks: [new PlaybackSubtitleTrack(7, "zh-CN", "简体中文", "subrip", true, "episode.zh-CN.srt", true)],
                SelectedSubtitleTrackId: 7,
                AudioTracks: [new MpvAudioTrack(2, "jpn", "日语", "aac", false, true)],
                SelectedAudioTrackId: 2),
            (episodeId, positionMs) =>
            {
                requestedEpisode = episodeId;
                requestedPosition = positionMs;
                return Task.FromResult(true);
            },
            command =>
            {
                requestedCommand = command;
                return command.Command switch
                {
                    "pipe_error" => Task.FromException<PlaybackRuntimeStatus>(new IOException("pipe closed")),
                    "internal_error" => Task.FromException<PlaybackRuntimeStatus>(new System.Security.SecurityException("secret detail")),
                    _ => Task.FromResult(new PlaybackRuntimeStatus(State: "PAUSED", IsPlaying: false)),
                };
            },
            () => settings,
            update =>
            {
                Interlocked.Exchange(ref beforeSettingsUpdate, null)?.Invoke();
                settings = update(settings);
                return Task.FromResult(settings);
            },
            sourceActions,
            listenOnAnyIp: false,
            bangumiMetadata: bangumiMetadata,
            tmdbMetadata: tmdbMetadata,
            metadataTokens: metadataTokens,
            rssSubscriptions: rssSubscriptions,
            cloudDriveConfig: cloudDriveConfig,
            cloudDriveCredentials: cloudDriveCredentials,
            cloudDriveClient: cloudDriveClient,
            rssFeedClient: rssFeedClient,
            rssProcessed: rssProcessed,
            localLogs: localLogs,
            openObserveLogs: openObserveLogs,
            resolvePosterPath: (value, _) => Task.FromResult<string?>(
                value.ApiId == series.ApiId ? posterPath : null));
        await server.StartAsync();

        using var unauthorized = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        var webUi = await unauthorized.GetStringAsync("/");
        Assert.Contains("MiruPlay Web Control", webUi, StringComparison.Ordinal);
        Assert.Contains("/web/app.js", webUi, StringComparison.Ordinal);
        Assert.Equal("text/css", (await unauthorized.GetAsync("/web/app.css")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/javascript", (await unauthorized.GetAsync("/web/app.js")).Content.Headers.ContentType?.MediaType);
        var unauthorizedResponse = await unauthorized.GetAsync("/api/info");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
        Assert.False((await ReadJson(unauthorizedResponse)).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthorized.GetAsync($"/api/anime/{series.ApiId}/poster")).StatusCode);

        using var client = new HttpClient { BaseAddress = unauthorized.BaseAddress };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", tokens.AccessToken);
        var dsp = await ReadJson(await client.GetAsync("/api/audio-dsp"));
        Assert.False(dsp.RootElement.GetProperty("data").GetProperty("config").GetProperty("enabled").GetBoolean());
        var rewImport = await client.PostAsJsonAsync("/api/audio-dsp/import-rew", new
        {
            target = "left",
            content = "Generic\nType\tEnabled\tFrequency(Hz)\tGain(dB)\tQ\nPK\tTrue\t70\t-14.7\t10.398",
        });
        Assert.Equal(HttpStatusCode.OK, rewImport.StatusCode);
        Assert.Single((await ReadJson(rewImport)).RootElement.GetProperty("data").GetProperty("bands").EnumerateArray());
        var invalidDsp = await client.PutAsJsonAsync("/api/audio-dsp", new
        {
            config = new { schemaVersion = 1, enabled = true, selectedPresetId = "missing", presets = Array.Empty<object>() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidDsp.StatusCode);
        var sources = await ReadJson(await client.GetAsync("/api/sources"));
        Assert.Single(sources.RootElement.GetProperty("data").EnumerateArray());
        var info = await ReadJson(await client.GetAsync("/api/info"));
        var capabilities = info.RootElement.GetProperty("data").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("logUpload").GetBoolean());
        Assert.False(capabilities.GetProperty("appUpdate").GetBoolean());
        Assert.False(capabilities.GetProperty("formatAwareToneMapping").GetBoolean());
        Assert.False(capabilities.GetProperty("backgroundTasks").GetBoolean());
        var logs = await ReadJson(await client.GetAsync("/api/logs?limit=1"));
        Assert.Equal(1, logs.RootElement.GetProperty("data").GetProperty("records").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks")).StatusCode);
        var appControl = await ReadJson(await client.PostAsJsonAsync("/api/app-control", new { action = "restart" }));
        Assert.False(appControl.RootElement.GetProperty("data").GetProperty("accepted").GetBoolean());
        var addSource = await client.PostAsJsonAsync("/api/sources", new
        {
            name = "Anime",
            type = "LOCAL",
            location = _directory,
            contentMode = "ANIME",
            recognitionMode = "MLIP",
        });
        Assert.Equal(HttpStatusCode.OK, addSource.StatusCode);
        Assert.Equal("MLIP", requestedSource?.RecognitionMode);
        var addDirectorySource = await client.PostAsJsonAsync("/api/sources", new
        {
            name = "Directory Anime",
            type = "LOCAL",
            location = Path.Combine(_directory, "directory"),
            contentMode = "ANIME",
            recognitionMode = "DIRECTORY",
        });
        Assert.Equal(HttpStatusCode.OK, addDirectorySource.StatusCode);
        Assert.Equal("DIRECTORY", requestedSource?.RecognitionMode);
        var localDirectories = await client.GetAsync($"/api/local-directories?path={Uri.EscapeDataString(_directory)}");
        Assert.Equal(HttpStatusCode.OK, localDirectories.StatusCode);

        var cloudDrive = await ReadJson(await client.GetAsync("/api/cloud-drive"));
        Assert.False(cloudDrive.RootElement.GetProperty("data").GetProperty("config").GetProperty("enabled").GetBoolean());
        Assert.Empty(cloudDrive.RootElement.GetProperty("data").GetProperty("subscriptions").EnumerateArray());
        var saveCloudConfig = await ReadJson(await client.PutAsJsonAsync("/api/cloud-drive/config", new
        {
            endpointUrl = "http://localhost:19798",
            username = "cloud-user",
            webDavSourceId = 1,
            inboxPath = "/Anime/Downloads",
            libraryPath = "/Anime/Library",
            libraryMode = "SINGLE_DIRECTORY",
            intervalMinutes = 15,
            enabled = true,
            rssProxyEnabled = false,
            rssProxyHost = "",
            rssProxyPort = 1080,
        }));
        Assert.True(saveCloudConfig.RootElement.GetProperty("data").GetProperty("config").GetProperty("enabled").GetBoolean());
        Assert.Equal("SINGLE_DIRECTORY", saveCloudConfig.RootElement.GetProperty("data").GetProperty("config").GetProperty("libraryMode").GetString());
        var loginCloudDrive = await ReadJson(await client.PostAsJsonAsync("/api/cloud-drive/login", new
        {
            endpointUrl = "http://localhost:19798",
            username = "cloud-user",
            password = "cloud-password-secret",
        }));
        Assert.True(loginCloudDrive.RootElement.GetProperty("data").GetProperty("tokenConfigured").GetBoolean());
        Assert.DoesNotContain("issued-cloud-token", loginCloudDrive.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("issued-cloud-token", cloudDriveCredentials.Load().Token);
        Assert.Equal("cloud-password-secret", cloudDriveCredentials.Load().Password);
        var verifyCloudToken = await ReadJson(await client.PostAsJsonAsync("/api/cloud-drive/token", new
        {
            endpointUrl = "http://localhost:19798",
            token = "manual-cloud-token",
        }));
        Assert.Equal("MiruPlay", verifyCloudToken.RootElement.GetProperty("data").GetProperty("friendlyName").GetString());
        Assert.True(verifyCloudToken.RootElement.GetProperty("data").GetProperty("allowAddOfflineDownload").GetBoolean());
        Assert.DoesNotContain("manual-cloud-token", verifyCloudToken.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("manual-cloud-token", cloudDriveCredentials.Load().Token);
        var cloudDirectories = await ReadJson(await client.GetAsync("/api/cloud-drive/directories?path=%2FOutside"));
        Assert.Equal("/Anime", cloudDirectories.RootElement.GetProperty("data").GetProperty("path").GetString());
        Assert.Null(cloudDirectories.RootElement.GetProperty("data").GetProperty("parentPath").GetString());
        var directoryEntries = cloudDirectories.RootElement.GetProperty("data").GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["Season 1", "Season 2"], directoryEntries.Select(item => item.GetProperty("name").GetString()));
        var offlineSubmit = await ReadJson(await client.PostAsJsonAsync("/api/cloud-drive/offline", new
        {
            urls = new List<string> { "magnet:?xt=urn:btih:abc", "https://example.test/file.torrent" },
            targetFolder = "/Anime/Downloads",
        }));
        Assert.Equal(2, offlineSubmit.RootElement.GetProperty("data").GetProperty("submitted").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/cloud-drive/offline", new
        {
            urls = new List<string> { "magnet:?xt=urn:btih:abc" },
            targetFolder = "/Outside",
        })).StatusCode);
        var addRss = await ReadJson(await client.PostAsJsonAsync("/api/cloud-drive/rss", new
        {
            id = 0,
            name = "Anime",
            url = "https://example.test/feed.xml",
            filterRegex = "Frieren",
            enabled = true,
        }));
        var rssId = addRss.RootElement.GetProperty("data").GetProperty("id").GetInt64();
        var rssPreview = await ReadJson(await client.PostAsync($"/api/cloud-drive/rss/{rssId}/preview", null));
        Assert.Equal(3, rssPreview.RootElement.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(1, rssPreview.RootElement.GetProperty("data").GetProperty("wouldSubmit").GetInt32());
        Assert.Equal(1, rssPreview.RootElement.GetProperty("data").GetProperty("skipped").GetInt32());
        Assert.Equal(1, rssPreview.RootElement.GetProperty("data").GetProperty("missing").GetInt32());
        Assert.False(rssPreview.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("processed").GetBoolean());
        var cloudRun = await ReadJson(await client.PostAsync("/api/cloud-drive/run", null));
        Assert.Equal("SUCCEEDED", cloudRun.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1, cloudRun.RootElement.GetProperty("data").GetProperty("summary").GetProperty("submitted").GetInt32());
        Assert.Equal(1, cloudRun.RootElement.GetProperty("data").GetProperty("summary").GetProperty("skipped").GetInt32());
        Assert.Equal(1, cloudRun.RootElement.GetProperty("data").GetProperty("summary").GetProperty("failed").GetInt32());
        var cloudRunStatus = await ReadJson(await client.GetAsync("/api/cloud-drive/run"));
        Assert.Equal("SUCCEEDED", cloudRunStatus.RootElement.GetProperty("data").GetProperty("status").GetString());
        var updateRss = await client.PutAsJsonAsync($"/api/cloud-drive/rss/{rssId}", new
        {
            id = rssId,
            name = "Anime updated",
            url = "https://example.test/new.xml",
            filterRegex = (string?)null,
            enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, updateRss.StatusCode);
        Assert.False(Assert.Single(rssSubscriptions.List()).Enabled);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/cloud-drive/rss/{rssId}")).StatusCode);
        Assert.Empty(rssSubscriptions.List());
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/settings/cloud-drive/credentials")).StatusCode);
        Assert.Null(cloudDriveCredentials.Load().Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/cloud-drive/rss", new
        {
            id = 0,
            name = "Bad",
            url = "file:///etc/passwd",
            enabled = true,
        })).StatusCode);

        var library = await ReadJson(await client.GetAsync("/api/library?query=Frieren"));
        var anime = Assert.Single(library.RootElement.GetProperty("data").GetProperty("allAnime").EnumerateArray());
        Assert.Equal(series.ApiId, anime.GetProperty("id").GetString());
        Assert.Equal(431767, anime.GetProperty("bangumiId").GetInt32());
        Assert.Equal(209867, anime.GetProperty("tmdbId").GetInt32());
        Assert.Equal(3, anime.GetProperty("externalIds").GetArrayLength());
        Assert.Equal($"/api/anime/{Uri.EscapeDataString(series.ApiId)}/poster", anime.GetProperty("posterUrl").GetString());
        var poster = await client.GetAsync(anime.GetProperty("posterUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, poster.StatusCode);
        Assert.Equal("image/png", poster.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], await poster.Content.ReadAsByteArrayAsync());
        var detail = await ReadJson(await client.GetAsync($"/api/anime/{Uri.EscapeDataString(series.ApiId)}"));
        var detailEpisode = Assert.Single(detail.RootElement.GetProperty("data").GetProperty("episodes").EnumerateArray())
            .GetProperty("episode");
        Assert.Equal(12345, detailEpisode.GetProperty("bangumiEpisodeId").GetInt32());
        var metadataSearch = await ReadJson(await client.GetAsync("/api/metadata/bangumi/search?query=Frieren&limit=5"));
        var candidate = Assert.Single(metadataSearch.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(431767, candidate.GetProperty("id").GetInt32());
        Assert.Equal("Bangumi", candidate.GetProperty("provider").GetString());
        var invalidMetadataSearch = await client.GetAsync("/api/metadata/bangumi/search?query=%20");
        Assert.Equal(HttpStatusCode.BadRequest, invalidMetadataSearch.StatusCode);
        var metadataSettings = await ReadJson(await client.GetAsync("/api/settings/metadata"));
        Assert.False(metadataSettings.RootElement.GetProperty("data").GetProperty("tmdbTokenConfigured").GetBoolean());
        Assert.False(metadataSettings.RootElement.GetProperty("data").GetProperty("bangumiTokenConfigured").GetBoolean());
        Assert.DoesNotContain("token-value", metadataSettings.RootElement.GetRawText(), StringComparison.Ordinal);
        var missingBangumiToken = await client.GetAsync("/api/metadata/bangumi/me");
        Assert.Equal(HttpStatusCode.Conflict, missingBangumiToken.StatusCode);
        var missingCollectionToken = await client.GetAsync("/api/metadata/bangumi/subjects/431767/collection");
        Assert.Equal(HttpStatusCode.Conflict, missingCollectionToken.StatusCode);
        var missingEpisodeReadToken = await client.GetAsync("/api/metadata/bangumi/subjects/431767/episodes");
        Assert.Equal(HttpStatusCode.Conflict, missingEpisodeReadToken.StatusCode);
        var missingCollectionWriteToken = await client.PutAsJsonAsync(
            "/api/metadata/bangumi/subjects/431767/collection",
            new { type = 3 });
        Assert.Equal(HttpStatusCode.Conflict, missingCollectionWriteToken.StatusCode);
        var saveBangumiToken = await client.PutAsJsonAsync(
            "/api/settings/metadata/bangumi-token",
            new { token = "bangumi-token-value" });
        Assert.Equal(HttpStatusCode.OK, saveBangumiToken.StatusCode);
        Assert.Equal("bangumi-token-value", metadataTokens.Load().Bangumi);
        var clearBangumiToken = await client.DeleteAsync("/api/settings/metadata/bangumi-token");
        Assert.Equal(HttpStatusCode.OK, clearBangumiToken.StatusCode);
        Assert.Null(metadataTokens.Load().Bangumi);
        var missingTmdbToken = await client.GetAsync("/api/metadata/tmdb/search?query=Frieren");
        Assert.Equal(HttpStatusCode.Conflict, missingTmdbToken.StatusCode);
        var saveTmdbToken = await client.PutAsJsonAsync(
            "/api/settings/metadata/tmdb-token",
            new { token = "token-value" });
        Assert.Equal(HttpStatusCode.OK, saveTmdbToken.StatusCode);
        var tmdbSearch = await ReadJson(await client.GetAsync("/api/metadata/tmdb/search?query=Frieren&year=2023"));
        Assert.Equal(209867, Assert.Single(tmdbSearch.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetInt32());
        var clearTmdbToken = await client.DeleteAsync("/api/settings/metadata/tmdb-token");
        Assert.Equal(HttpStatusCode.OK, clearTmdbToken.StatusCode);
        Assert.Null(metadataTokens.Load().Tmdb);

        var playResponse = await client.PostAsJsonAsync("/api/playback/play", new
        {
            episodeId = episode.ApiId,
            startPositionMs = 12_345,
        });
        Assert.Equal(HttpStatusCode.OK, playResponse.StatusCode);
        Assert.Equal(episode.ApiId, requestedEpisode);
        Assert.Equal(12_345, requestedPosition);

        var commandResponse = await client.PostAsJsonAsync("/api/playback/command", new
        {
            command = "seek_relative",
            deltaMs = -5_000,
        });
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        Assert.Equal(new PlaybackControlCommand("seek_relative", DeltaMs: -5_000), requestedCommand);

        var playbackStatus = await ReadJson(await client.GetAsync("/api/playback/status"));
        var statusData = playbackStatus.RootElement.GetProperty("data");
        Assert.Equal(7, statusData.GetProperty("selectedSubtitleTrackId").GetInt32());
        Assert.Equal("第 1 集 · 测试分集", statusData.GetProperty("title").GetString());
        Assert.Equal("简体中文（外挂）", Assert.Single(statusData.GetProperty("subtitleTracks").EnumerateArray()).GetProperty("displayLabel").GetString());
        Assert.Equal(2, statusData.GetProperty("selectedAudioTrackId").GetInt32());
        Assert.Equal("日语", Assert.Single(statusData.GetProperty("audioTracks").EnumerateArray()).GetProperty("displayLabel").GetString());
        var subtitleCommand = await client.PostAsJsonAsync("/api/playback/command", new
        {
            command = "subtitle",
            subtitleTrackId = 7,
        });
        Assert.Equal(HttpStatusCode.OK, subtitleCommand.StatusCode);
        Assert.Equal(new PlaybackControlCommand("subtitle", SubtitleTrackId: 7), requestedCommand);

        var audioCommand = await client.PostAsJsonAsync("/api/playback/command", new
        {
            command = "audio",
            audioTrackId = 2,
        });
        Assert.Equal(HttpStatusCode.OK, audioCommand.StatusCode);
        Assert.Equal(new PlaybackControlCommand("audio", AudioTrackId: 2), requestedCommand);

        var missingCommand = await client.PostAsJsonAsync("/api/playback/command", new { deltaMs = 1_000 });
        Assert.Equal(HttpStatusCode.BadRequest, missingCommand.StatusCode);
        Assert.False((await ReadJson(missingCommand)).RootElement.GetProperty("ok").GetBoolean());

        var pipeError = await client.PostAsJsonAsync("/api/playback/command", new { command = "pipe_error" });
        Assert.Equal(HttpStatusCode.Conflict, pipeError.StatusCode);

        var internalError = await client.PostAsJsonAsync("/api/playback/command", new { command = "internal_error" });
        Assert.Equal(HttpStatusCode.InternalServerError, internalError.StatusCode);
        Assert.Equal("服务器内部错误", (await ReadJson(internalError)).RootElement.GetProperty("error").GetString());

        var missingAnime = await client.GetAsync("/api/anime/missing");
        Assert.Equal(HttpStatusCode.NotFound, missingAnime.StatusCode);

        var settingsResponse = await client.PutAsJsonAsync("/api/settings/playback", new
        {
            endAction = "play_next_episode",
            preferredSubtitleLanguage = "zh_hans",
        });
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        Assert.Equal("play_next_episode", settings.PlaybackEndAction);
        Assert.Equal("zh_hans", settings.PreferredSubtitleLanguage);
        var playbackSettings = await ReadJson(await client.GetAsync("/api/settings/playback"));
        Assert.False(
            playbackSettings.RootElement.GetProperty("data").GetProperty("formatAwareToneMapping").GetProperty("supported").GetBoolean());
        var unsupportedToneMapping = await client.PutAsJsonAsync("/api/settings/playback", new
        {
            formatAwareToneMapping = new { defaultBackend = "EXPERIMENTAL_MPV_EMBEDDED" },
        });
        Assert.Equal(HttpStatusCode.NotImplemented, unsupportedToneMapping.StatusCode);

        var updateStatus = await ReadJson(await client.GetAsync("/api/app-update"));
        Assert.False(updateStatus.RootElement.GetProperty("data").GetProperty("supported").GetBoolean());
        var updateCheck = await ReadJson(await client.PostAsync("/api/app-update/check", null));
        Assert.False(updateCheck.RootElement.GetProperty("data").GetProperty("updateAvailable").GetBoolean());
        var updateDownload = await ReadJson(await client.PostAsync("/api/app-update/download", null));
        Assert.Contains("尚未配置", updateDownload.RootElement.GetProperty("data").GetProperty("lastError").GetString(), StringComparison.Ordinal);

        foreach (var (method, path) in new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "/api/app-update/install-permission"),
            (HttpMethod.Get, "/api/playback/clock-samples"),
            (HttpMethod.Get, "/api/playback/native-diagnostics"),
            (HttpMethod.Post, "/api/playback/native-profile"),
            (HttpMethod.Get, "/api/playback/native-profile/download"),
            (HttpMethod.Post, "/api/playback/profile"),
            (HttpMethod.Get, "/api/playback/debug-config"),
            (HttpMethod.Put, "/api/playback/debug-config"),
        })
        {
            using var unsupportedRequest = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(unsupportedRequest);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.False((await ReadJson(response)).RootElement.GetProperty("ok").GetBoolean());
        }

        var scanSettings = await ReadJson(await client.PutAsJsonAsync("/api/settings/scan", new
        {
            currentAppMode = "drama",
        }));
        Assert.Equal("drama", settings.CurrentAppMode);
        Assert.Equal("drama", scanSettings.RootElement.GetProperty("data").GetProperty("currentAppMode").GetString());
        Assert.Collection(
            scanSettings.RootElement.GetProperty("data").GetProperty("appModeOptions").EnumerateArray(),
            item => Assert.Equal("anime", item.GetString()),
            item => Assert.Equal("drama", item.GetString()));
        var automaticScan = await ReadJson(await client.PutAsJsonAsync("/api/settings/scan", new
        {
            autoScanEnabled = true,
            autoScanIntervalHours = 12,
        }));
        Assert.True(settings.AutoScanEnabled);
        Assert.Equal(12, settings.AutoScanIntervalHours);
        Assert.True(automaticScan.RootElement.GetProperty("data").GetProperty("autoScanEnabled").GetBoolean());
        Assert.Equal(12, automaticScan.RootElement.GetProperty("data").GetProperty("autoScanIntervalHours").GetInt32());
        Assert.Equal(
            [1, 6, 12, 24],
            automaticScan.RootElement.GetProperty("data").GetProperty("autoScanIntervalOptionsHours")
                .EnumerateArray().Select(item => item.GetInt32()).ToArray());
        var invalidAutomaticScan = await client.PutAsJsonAsync("/api/settings/scan", new { autoScanIntervalHours = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAutomaticScan.StatusCode);
        beforeSettingsUpdate = () => settings = settings with { PlayerPath = @"C:\Players\mpv.exe" };
        var concurrentPlaybackUpdate = client.PutAsJsonAsync("/api/settings/playback", new
        {
            endAction = "return_to_detail",
            preferredSubtitleLanguage = "ja",
        });
        var concurrentModeUpdate = client.PutAsJsonAsync("/api/settings/scan", new { currentAppMode = "anime" });
        var concurrentResponses = await Task.WhenAll(concurrentPlaybackUpdate, concurrentModeUpdate);
        Assert.All(concurrentResponses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal("return_to_detail", settings.PlaybackEndAction);
        Assert.Equal("ja", settings.PreferredSubtitleLanguage);
        Assert.Equal("anime", settings.CurrentAppMode);
        Assert.Equal(@"C:\Players\mpv.exe", settings.PlayerPath);

        using var queryClient = new HttpClient { BaseAddress = unauthorized.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await queryClient.GetAsync($"/api/info?token={Uri.EscapeDataString(tokens.AccessToken)}")).StatusCode);
        queryClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await queryClient.GetAsync("/api/info")).StatusCode);
        queryClient.DefaultRequestHeaders.Authorization = null;
        queryClient.DefaultRequestHeaders.Add("Cookie", $"miruplay_web_token={tokens.AccessToken}");
        Assert.Equal(HttpStatusCode.OK, (await queryClient.GetAsync("/api/info")).StatusCode);
    }

    [Fact]
    public void TokenStorePersistsRotatesAndUsesFixedValueMatching()
    {
        var path = Path.Combine(_directory, "token.bin");
        var first = new WebControlTokenStore(path);
        var original = first.AccessToken;

        Assert.Equal(32, original.Length);
        Assert.True(first.Matches(original));
        Assert.False(first.Matches($"{original}x"));
        Assert.Equal(original, new WebControlTokenStore(path).AccessToken);
        Assert.NotEqual(original, first.Rotate());
    }

    private static LibrarySeries Series(LibraryEpisode episode) => new(
        1,
        "series-uuid",
        "Frieren",
        "Sousou no Frieren",
        "Summary",
        2023,
        "2023-09-29",
        ["Adventure"],
        null,
        [episode],
        [])
    {
        ExternalIds =
        [
            new("Bangumi", "431767"),
            new("TMDB", "209867"),
            new("AniDB", "18597"),
        ],
    };

    private static LibraryEpisode Episode() => new(
        1,
        "episode-uuid",
        "episode-key",
        1,
        1,
        1,
        "The Journey's End",
        "C:\\Media\\Frieren\\01.mkv",
        TimeSpan.FromMinutes(24),
        [])
    {
        ExternalIds = [new("Bangumi", "12345")],
    };

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
