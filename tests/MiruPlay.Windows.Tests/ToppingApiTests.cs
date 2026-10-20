using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class ToppingApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-topping-api-{Guid.NewGuid():N}");

    public ToppingApiTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ToppingApiReportsStatusAndEnforcesVolumeConfirmation()
    {
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "token.bin"));
        var device = new ToppingHidDevice("hid-path", "TOPPING DX5 II", ToppingProtocol.Dx5II);
        var transport = new RecordingTransport([device]);
        var settings = new AppSettings(WebControlPort: port);
        var preset = new AudioDspPreset(
            "flat-test", "Flat test",
            Rules: [new AudioDspChannelRule(Bands: [new AudioDspBand(AudioDspFilterType.Peaking, 1_000, -3, 1)])]);
        await using var server = new WebControlServer(
            port,
            tokens,
            () => [],
            () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false),
            command => Task.FromResult(new PlaybackRuntimeStatus()),
            () => settings,
            update =>
            {
                settings = update(settings);
                return Task.FromResult(settings);
            },
            new MediaSourceActions(
                () => [],
                _ => Task.FromResult(new SourceTestResponse(true, "ok")),
                _ => throw new NotSupportedException(),
                (_, _) => throw new NotSupportedException(),
                _ => Task.CompletedTask,
                (_, _) => throw new NotSupportedException()),
            listenOnAnyIp: false,
            topping: new ToppingController(transport),
            findAudioPreset: id => id == preset.Id ? preset : null);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", tokens.AccessToken);

        var status = await ReadData(await client.GetAsync("/api/topping/status"));
        Assert.True(status.GetProperty("attached").GetBoolean());
        Assert.Equal("Topping DX5 II", status.GetProperty("model").GetString());

        var loud = await client.PostAsJsonAsync("/api/topping/volume", new { db = -5.0 });
        Assert.Equal(HttpStatusCode.BadRequest, loud.StatusCode);
        Assert.Empty(transport.Sends);

        var volume = await client.PostAsJsonAsync("/api/topping/volume", new { db = -5.0, confirm = true });
        Assert.Equal(HttpStatusCode.OK, volume.StatusCode);
        Assert.Equal(-5, (await ReadData(volume)).GetProperty("volumeDb").GetDouble());

        var gain = await client.PostAsJsonAsync("/api/topping/gain", new { on = true });
        Assert.Equal(HttpStatusCode.OK, gain.StatusCode);

        var apply = await client.PostAsJsonAsync("/api/topping/preset", new { presetId = "flat-test" });
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.Empty((await ReadData(apply)).GetProperty("warnings").EnumerateArray());

        var missing = await client.PostAsJsonAsync("/api/topping/preset", new { presetId = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var flat = await client.PostAsJsonAsync("/api/topping/flat", new { });
        Assert.Equal(HttpStatusCode.OK, flat.StatusCode);
        Assert.True(transport.Sends.Count > 100); // 11 registers x 10 frames + commits.
    }

    [Fact]
    public async Task ToppingApiWithoutHardwareReportsUnattached()
    {
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "unattached-token.bin"));
        var settings = new AppSettings(WebControlPort: port);
        await using var server = new WebControlServer(
            port,
            tokens,
            () => [],
            () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false),
            command => Task.FromResult(new PlaybackRuntimeStatus()),
            () => settings,
            update =>
            {
                settings = update(settings);
                return Task.FromResult(settings);
            },
            new MediaSourceActions(
                () => [],
                _ => Task.FromResult(new SourceTestResponse(true, "ok")),
                _ => throw new NotSupportedException(),
                (_, _) => throw new NotSupportedException(),
                _ => Task.CompletedTask,
                (_, _) => throw new NotSupportedException()),
            listenOnAnyIp: false,
            topping: new ToppingController(new RecordingTransport([])));
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", tokens.AccessToken);

        var status = await ReadData(await client.GetAsync("/api/topping/status"));
        Assert.False(status.GetProperty("attached").GetBoolean());
        var volume = await client.PostAsJsonAsync("/api/topping/volume", new { db = -20.0 });
        Assert.Equal(HttpStatusCode.BadRequest, volume.StatusCode);
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

    private sealed class RecordingTransport(IReadOnlyList<ToppingHidDevice> devices) : IToppingHidTransport
    {
        public List<byte[]> Sends { get; } = [];
        public IReadOnlyList<ToppingHidDevice> ListDevices() => devices;
        public void SendFrame(ToppingHidDevice device, byte[] wireBytes) => Sends.Add(wireBytes.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
