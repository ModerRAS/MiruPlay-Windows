using System.Net.Http.Json;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AudioBalanceTests : IDisposable
{
    private static readonly string[] BalanceStorageValues = ["off", "low", "medium", "high"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miruplay-balance-" + Guid.NewGuid().ToString("N"));

    public AudioBalanceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void OffLevelProducesNoBalanceFilter()
    {
        Assert.Null(AudioBalanceFilterCompiler.Compile(AudioBalanceLevel.Off, 48_000));
        var graph = AudioDspFilterGraphCompiler.Compile(
            AudioDspConfig.Neutral(), AudioDspChannelLayout.Stereo, 48_000, AudioBalanceLevel.Off);
        Assert.Empty(graph.MpvArguments);
        Assert.Equal("disabled", graph.EffectiveRoute);
    }

    [Fact]
    public void LevelsMapToMonotonicDynaudnormParameters()
    {
        var low = AudioBalanceFilterCompiler.ForLevel(AudioBalanceLevel.Low);
        var medium = AudioBalanceFilterCompiler.ForLevel(AudioBalanceLevel.Medium);
        var high = AudioBalanceFilterCompiler.ForLevel(AudioBalanceLevel.High);

        Assert.True(low.MaxBoostFactor < medium.MaxBoostFactor && medium.MaxBoostFactor < high.MaxBoostFactor);
        Assert.True(low.FrameMs > medium.FrameMs && medium.FrameMs > high.FrameMs);
        Assert.True(low.GaussianWindow > medium.GaussianWindow && medium.GaussianWindow > high.GaussianWindow);
        Assert.All(new[] { low, medium, high }, parameters => Assert.Equal(0.95, parameters.PeakCeiling));
    }

    [Fact]
    public void BalanceOnlyGraphUsesDynaudnormAndFloatOutput()
    {
        var graph = AudioDspFilterGraphCompiler.Compile(
            AudioDspConfig.Neutral(), AudioDspChannelLayout.Stereo, 48_000, AudioBalanceLevel.High);

        Assert.Equal("balance", graph.EffectiveRoute);
        Assert.Contains("dynaudnorm=f=120:g=9:m=5:r=0.1:p=0.95:n=1", graph.AfValue, StringComparison.Ordinal);
        Assert.Contains("--audio-format=float", graph.MpvArguments);
        Assert.Contains(graph.MpvArguments, argument => argument.StartsWith("--af=lavfi=[dynaudnorm=", StringComparison.Ordinal));
    }

    [Fact]
    public void BalanceFilterRunsAfterDspPeqChain()
    {
        var config = new AudioDspConfig(
            true,
            "stereo",
            [
                new(
                    "stereo",
                    "Stereo",
                    Rules: [new(AudioDspChannelTarget.Left, [new(GainDb: -6)])],
                    Limiter: new(Enabled: true)),
            ]);
        var graph = AudioDspFilterGraphCompiler.Compile(
            config, AudioDspChannelLayout.Stereo, 48_000, AudioBalanceLevel.Medium);

        var peqIndex = graph.AfValue.IndexOf("firequalizer", StringComparison.Ordinal);
        var limiterIndex = graph.AfValue.IndexOf("alimiter", StringComparison.Ordinal);
        var balanceIndex = graph.AfValue.IndexOf("dynaudnorm", StringComparison.Ordinal);
        Assert.True(peqIndex >= 0 && limiterIndex > peqIndex && balanceIndex > limiterIndex);
    }

    [Theory]
    [InlineData("off", AudioBalanceLevel.Off)]
    [InlineData("low", AudioBalanceLevel.Low)]
    [InlineData("MEDIUM", AudioBalanceLevel.Medium)]
    [InlineData("high", AudioBalanceLevel.High)]
    [InlineData("bogus", AudioBalanceLevel.Off)]
    [InlineData(null, AudioBalanceLevel.Off)]
    public void StorageValuesNormalizeLikeAndroid(string? value, AudioBalanceLevel expected) =>
        Assert.Equal(expected, AudioBalanceLevels.FromStorageValue(value));

    [Fact]
    public void SettingsRoundTripAndLegacyFileDefaultToOff()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings(AudioBalance: "high"));
        Assert.Equal("high", store.Load().AudioBalance);

        File.WriteAllText(path, "{\"PreferredSubtitleLanguage\":\"auto\"}");
        Assert.Equal("off", store.Load().AudioBalance);

        File.WriteAllText(path, "{\"AudioBalance\":\"ULTRA\"}");
        Assert.Equal("off", store.Load().AudioBalance);
    }

    [Fact]
    public async Task PlaybackSettingsApiExposesAndPersistsAudioBalance()
    {
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "web-token.bin"));
        var settings = new AppSettings(WebControlPort: port);
        AppSettings? applied = null;
        await using var server = new WebControlServer(
            port,
            tokens,
            () => [],
            () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false),
            _ => Task.FromResult(new PlaybackRuntimeStatus()),
            () => settings,
            update =>
            {
                settings = update(settings);
                return Task.FromResult(settings);
            },
            MediaSourceActionsStub(),
            listenOnAnyIp: false,
            metadataTokens: new MetadataTokenStore(Path.Combine(_directory, "metadata-tokens.bin")),
            bangumiArchive: new BangumiArchiveStore(Path.Combine(_directory, "bangumi-archive")),
            localLogs: new RotatingLocalLogStore(Path.Combine(_directory, "logs", "miruplay.jsonl")),
            tasks: new HeadlessTaskScheduler(storePath: Path.Combine(_directory, "tasks.json")),
            applyPlaybackSettings: value =>
            {
                applied = value;
                return Task.CompletedTask;
            });
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", tokens.AccessToken);

        var initial = await ReadData(await client.GetAsync("/api/settings/playback"));
        Assert.Equal("off", initial.GetProperty("audioBalance").GetString());
        Assert.Equal(
            BalanceStorageValues,
            initial.GetProperty("audioBalanceOptions").EnumerateArray().Select(item => item.GetString()).ToArray());

        var updateResponse = await client.PutAsJsonAsync("/api/settings/playback", new { audioBalance = "high" });
        updateResponse.EnsureSuccessStatusCode();
        var updated = await ReadData(updateResponse);
        Assert.Equal("high", updated.GetProperty("audioBalance").GetString());
        Assert.Equal("high", settings.AudioBalance);
        Assert.Equal("high", applied?.AudioBalance);

        var invalid = await client.PutAsJsonAsync("/api/settings/playback", new { audioBalance = "ultra" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static MediaSourceActions MediaSourceActionsStub() => new(
        () => [],
        _ => Task.FromResult(new SourceTestResponse(true, "ok")),
        request => Task.FromResult(new MediaSourceInfoDto(1, request.Name, request.Type, request.ContentMode, new Dictionary<string, string>(), true, 0)),
        (_, request) => Task.FromResult(new MediaSourceInfoDto(1, request.Name, request.Type, request.ContentMode, new Dictionary<string, string>(), true, 0)),
        _ => Task.CompletedTask,
        (id, _) => Task.FromResult(new SourceScanResponse(id, "source", 0, 0, 0)));

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return envelope.RootElement.GetProperty("data").Clone();
    }

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
