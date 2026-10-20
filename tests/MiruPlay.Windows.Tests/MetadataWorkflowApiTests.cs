using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MetadataWorkflowApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-metadata-api-{Guid.NewGuid():N}");
    private static readonly string[] Genres = ["Drama"];

    public MetadataWorkflowApiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ApiPreviewsAppliesAndUndoesWritableDirectoryMetadata()
    {
        var mediaRoot = Path.Combine(_root, "media");
        var showRoot = Path.Combine(mediaRoot, "Show");
        Directory.CreateDirectory(showRoot);
        File.WriteAllText(Path.Combine(showRoot, "Show.S01E01.mkv"), "test media");
        File.WriteAllText(Path.Combine(showRoot, "tvshow.nfo"), "<tvshow><title>NFO title</title><originaltitle>Original</originaltitle></tvshow>");
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: [], AudioDsp: AudioDspConfig.Neutral());
        var registry = new MediaSourceRegistry(() => settings, update => settings = update(settings),
            new MediaSourceCredentialStore(Path.Combine(_root, "credentials")),
            directoryIndex: new DirectoryLibraryIndex(Path.Combine(_root, "index.db")));
        var tokens = new WebControlTokenStore(Path.Combine(_root, "token.bin"));
        var port = ReservePort();
        var archiveStore = new BangumiArchiveStore(Path.Combine(_root, "archive"));
        var metadataTokens = new MetadataTokenStore(Path.Combine(_root, "metadata-tokens.bin"));
        metadataTokens.SaveTmdb("test-token");
        using var bangumiClient = new BangumiMetadataClient(new JsonHandler("""{"data":[{"id":12,"type":2,"name":"Example Show","name_cn":"示例作品","summary":"Summary","date":"1998-04-03"}]}"""), new("https://bgm.test/v0/"));
        using var tmdbClient = new TmdbMetadataClient(new JsonHandler("""{"results":[{"id":34,"name":"Example Show","original_name":"Example Show","overview":"Summary","first_air_date":"1998-04-03"}]}"""), new("https://tmdb.test/3/"));
        using var tvMazeClient = new TvMazeMetadataClient(new JsonHandler("""[{"show":{"id":56,"name":"Example Show","name_original":"Example Show","summary":"<p>Summary</p>","premiered":"1998-04-03"}}]"""), new("https://tvmaze.test/"));
        var sourceActions = new MediaSourceActions(registry.List, registry.TestAsync, registry.AddAsync, registry.UpdateAsync,
            id => { registry.Remove(id); return Task.CompletedTask; }, (id, token) => registry.ScanAsync(id, token));
        var refreshes = 0;
        var server = new WebControlServer(port, tokens, () => [], () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(new PlaybackRuntimeStatus()), () => settings,
            update => { settings = update(settings); return Task.FromResult(settings); }, sourceActions,
            listenOnAnyIp: false, metadataSources: registry, refreshSource: _ => { refreshes++; return Task.CompletedTask; },
            localLogs: new RotatingLocalLogStore(Path.Combine(_root, "logs.jsonl")),
            rssSubscriptions: new RssSubscriptionStore(Path.Combine(_root, "rss.json")),
            cloudDriveConfig: new CloudDriveAutomationStore(Path.Combine(_root, "cloud.json")),
            cloudDriveCredentials: new CloudDriveCredentialStore(Path.Combine(_root, "cloud-credentials")),
            bangumiArchive: archiveStore,
            bangumiMetadata: bangumiClient,
            tmdbMetadata: tmdbClient,
            tvMazeMetadata: tvMazeClient,
            metadataTokens: metadataTokens);
        await using (server)
        using (registry)
        {
            await server.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            client.DefaultRequestHeaders.Add("X-MiruPlay-Token", tokens.AccessToken);
            var addedResponse = await client.PostAsJsonAsync("/api/sources", new
            {
                name = "Editable directory", type = "LOCAL", location = mediaRoot,
                contentMode = "ANIME", recognitionMode = "DIRECTORY",
            });
            Assert.Equal(HttpStatusCode.OK, addedResponse.StatusCode);
            var sourceId = (await JsonDocument.ParseAsync(await addedResponse.Content.ReadAsStreamAsync()))
                .RootElement.GetProperty("data").GetProperty("id").GetInt64();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/sources/{sourceId}/scan", null)).StatusCode);
            var archiveStatus = await JsonDocument.ParseAsync(await (await client.GetAsync("/api/metadata/archive")).Content.ReadAsStreamAsync());
            Assert.False(archiveStatus.RootElement.GetProperty("data").GetProperty("hasSubjectData").GetBoolean());
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("{\"id\":12,\"type\":2,\"name\":\"Example Show\"}\n")), "file", "subject.jsonlines");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/metadata/archive/import", form)).StatusCode);
            var archiveSearch = await JsonDocument.ParseAsync(await (await client.GetAsync("/api/metadata/archive/search?query=Example%20Show")).Content.ReadAsStreamAsync());
            Assert.Equal("12", Assert.Single(archiveSearch.RootElement.GetProperty("data").EnumerateArray()).GetProperty("animeId").GetString());
            var aggregateResponse = await client.GetAsync("/api/metadata/search?query=Example%20Show&mode=drama&year=1998");
            Assert.Equal(HttpStatusCode.OK, aggregateResponse.StatusCode);
            var aggregate = await JsonDocument.ParseAsync(await aggregateResponse.Content.ReadAsStreamAsync());
            var aggregateCandidate = Assert.Single(aggregate.RootElement.GetProperty("data").GetProperty("candidates").EnumerateArray());
            Assert.Equal("AUTO_ACCEPT", aggregateCandidate.GetProperty("recommendation").GetString());
            Assert.Equal("drama", aggregateCandidate.GetProperty("mode").GetString());
            Assert.Equal(2, aggregateCandidate.GetProperty("providers").GetArrayLength());
            var catalogResponse = await client.GetAsync($"/api/metadata/sources/{sourceId}/library");
            Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
            var catalog = await JsonDocument.ParseAsync(await catalogResponse.Content.ReadAsStreamAsync());
            var seriesName = catalog.RootElement.GetProperty("data")[0];
            var seriesId = seriesName.GetProperty("id").GetString();
            Assert.Equal("Original", seriesName.GetProperty("title").GetString());
            Assert.Equal("NFO title", seriesName.GetProperty("titleCn").GetString());
            var previewResponse = await client.PostAsJsonAsync($"/api/metadata/sources/{sourceId}/preview", new[]
            {
                new { seriesId, metadata = new { title = "Manual title", originalTitle = "Original", plot = "Summary", premiered = "2026-09-01", genres = Genres, uniqueIds = new[] { new { type = "tvmaze", value = "123", isDefault = false } } } },
            });
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            var preview = await JsonDocument.ParseAsync(await previewResponse.Content.ReadAsStreamAsync());
            var batchId = preview.RootElement.GetProperty("data").GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/metadata/sources/{sourceId}/batches/{batchId}/apply", null)).StatusCode);
            catalog = await JsonDocument.ParseAsync(await (await client.GetAsync($"/api/metadata/sources/{sourceId}/library")).Content.ReadAsStreamAsync());
            var applied = catalog.RootElement.GetProperty("data")[0];
            Assert.Equal("Original", applied.GetProperty("title").GetString());
            Assert.Equal("Manual title", applied.GetProperty("titleCn").GetString());
            Assert.Contains(applied.GetProperty("metadataLinks").EnumerateArray(),
                item => item.GetProperty("url").GetString() == "https://www.tvmaze.com/shows/123");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/metadata/sources/{sourceId}/batches/{batchId}/undo", null)).StatusCode);
            catalog = await JsonDocument.ParseAsync(await (await client.GetAsync($"/api/metadata/sources/{sourceId}/library")).Content.ReadAsStreamAsync());
            var undone = catalog.RootElement.GetProperty("data")[0];
            Assert.Equal("Original", undone.GetProperty("title").GetString());
            Assert.Equal("NFO title", undone.GetProperty("titleCn").GetString());
            Assert.Equal(2, refreshes);
            var history = await JsonDocument.ParseAsync(await (await client.GetAsync($"/api/metadata/sources/{sourceId}/history")).Content.ReadAsStreamAsync());
            Assert.Equal("UNDONE", history.RootElement.GetProperty("data")[0].GetProperty("state").GetString());
            settings = settings with { MediaSources = settings.MediaSources!.Select(item => item with { RecognitionMode = "MLIP" }).ToList() };
            Assert.Throws<NotSupportedException>(() => registry.PreviewMetadata(sourceId, [new(seriesId!, new("Forbidden"))]));
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
