using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MusicApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-music-api-{Guid.NewGuid():N}");

    public MusicApiTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MusicApiScansLocalSourcesAndPlaysThroughTheInjectedHook()
    {
        var album = Path.Combine(_directory, "Album");
        Directory.CreateDirectory(album);
        File.WriteAllBytes(Path.Combine(album, "track.flac"), []);
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "token.bin"));
        var settings = new AppSettings(WebControlPort: port);
        var sourceInfo = new MediaSourceInfoDto(
            7, "Music", "LOCAL", "ANIME",
            new Dictionary<string, string> { ["path"] = _directory },
            true, 0);
        WebControlServer.MusicPlayRequest? played = null;
        await using var server = NewServer(port, tokens, settings, [sourceInfo], request =>
        {
            played = request;
            return Task.FromResult(true);
        });
        await server.StartAsync();

        using var client = AuthorizedClient(port, tokens.AccessToken);
        var albums = await ReadData(await client.GetAsync("/api/music/albums?sourceId=7"));
        var first = Assert.Single(albums.EnumerateArray());
        Assert.Equal("Album", first.GetProperty("title").GetString());
        var track = Assert.Single(first.GetProperty("tracks").EnumerateArray());

        var play = await client.PostAsJsonAsync("/api/music/play", new
        {
            sourceId = 7,
            file = track.GetProperty("filePath").GetString(),
            number = track.GetProperty("number").GetInt32(),
            title = track.GetProperty("title").GetString(),
            startMs = 0,
            endMs = -1,
        });
        Assert.Equal(HttpStatusCode.OK, play.StatusCode);
        Assert.Equal(7, played?.SourceId);

        var missingSource = await client.GetAsync("/api/music/albums?sourceId=99");
        Assert.Equal(HttpStatusCode.NotFound, missingSource.StatusCode);
    }

    [Fact]
    public async Task MusicPlayRejectsUnknownTracks()
    {
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "rejected-token.bin"));
        var settings = new AppSettings(WebControlPort: port);
        var sourceInfo = new MediaSourceInfoDto(
            7, "Music", "LOCAL", "ANIME",
            new Dictionary<string, string> { ["path"] = _directory },
            true, 0);
        await using var server = NewServer(port, tokens, settings, [sourceInfo], _ => Task.FromResult(false));
        await server.StartAsync();

        using var client = AuthorizedClient(port, tokens.AccessToken);
        var play = await client.PostAsJsonAsync("/api/music/play", new
        {
            sourceId = 7,
            file = Path.Combine(_directory, "missing.flac"),
            number = 1,
            title = "Missing",
            startMs = 0,
            endMs = -1,
        });
        Assert.Equal(HttpStatusCode.NotFound, play.StatusCode);
    }

    private static WebControlServer NewServer(
        int port,
        WebControlTokenStore tokens,
        AppSettings settings,
        IReadOnlyList<MediaSourceInfoDto> sources,
        Func<WebControlServer.MusicPlayRequest, Task<bool>> playMusic) =>
        new(
            port,
            tokens,
            () => [],
            () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false),
            command => Task.FromResult(new PlaybackRuntimeStatus()),
            () => settings,
            update => Task.FromResult(update(settings)),
            new MediaSourceActions(
                () => sources,
                _ => Task.FromResult(new SourceTestResponse(true, "ok")),
                _ => throw new NotSupportedException(),
                (_, _) => throw new NotSupportedException(),
                _ => Task.CompletedTask,
                (_, _) => throw new NotSupportedException()),
            listenOnAnyIp: false,
            musicLibrary: new MusicLibraryService(),
            playMusic: playMusic);

    private static HttpClient AuthorizedClient(int port, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", token);
        return client;
    }

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
