using System.Net.Http.Json;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvQualityEnhancementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miruplay-quality-" + Guid.NewGuid().ToString("N"));

    public MpvQualityEnhancementTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DefaultQualityOptionsKeepExistingArgumentsByteForByte()
    {
        var baseline = MpvWindowsVideoOptionMapper.BuildArguments(new MpvWindowsVideoOptions());
        Assert.Equal(["--vo=gpu-next", "--gpu-api=d3d11", "--hwdec=auto-safe"], baseline);

        var explicitOff = MpvWindowsVideoOptionMapper.BuildArguments(
            new MpvWindowsVideoOptions(Quality: new MpvQualityEnhancementOptions()));
        Assert.Equal(baseline, explicitOff);
    }

    [Fact]
    public void UpscaleQualityPresetsMapToExpectedNativeOptions()
    {
        Assert.Empty(MpvQualityEnhancementPlanner.BuildInitialArguments(
            new MpvQualityEnhancementOptions(MpvUpscaleQualityPreset.Off)));

        Assert.Equal(
        [
            "--scale=bilinear", "--cscale=bilinear", "--dscale=bilinear",
            "--correct-downscaling=no", "--linear-downscaling=no", "--sigmoid-upscaling=no",
        ], MpvQualityEnhancementPlanner.BuildInitialArguments(
            new MpvQualityEnhancementOptions(MpvUpscaleQualityPreset.Low)));

        Assert.Equal(
        [
            "--scale=lanczos", "--cscale=spline36", "--dscale=mitchell",
            "--correct-downscaling=yes", "--linear-downscaling=yes", "--sigmoid-upscaling=yes",
        ], MpvQualityEnhancementPlanner.BuildInitialArguments(
            new MpvQualityEnhancementOptions(MpvUpscaleQualityPreset.Medium)));

        Assert.Equal(
        [
            "--scale=ewa_lanczossharp", "--cscale=lanczos", "--dscale=mitchell", "--scale-antiring=0.6",
            "--correct-downscaling=yes", "--linear-downscaling=yes", "--sigmoid-upscaling=yes",
        ], MpvQualityEnhancementPlanner.BuildInitialArguments(
            new MpvQualityEnhancementOptions(MpvUpscaleQualityPreset.High)));
    }

    [Fact]
    public void InterpolationUsesDisplayResampleFrameMixingAndExtraHwdecFrames()
    {
        var arguments = MpvQualityEnhancementPlanner.BuildInitialArguments(
            new MpvQualityEnhancementOptions(Interpolation: MpvFrameInterpolationMode.MpvBuiltIn));

        Assert.Equal(
        [
            "--video-sync=display-resample",
            "--interpolation=yes",
            "--tscale=oversample",
            "--hwdec-extra-frames=8",
        ], arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("tscale=linear", StringComparison.Ordinal));

        // RIFE is a real mode now: it runs through the vapoursynth filter (added by the
        // playback session), not through mpv's built-in frame mixing arguments.
        Assert.Empty(MpvQualityEnhancementPlanner.BuildInitialArguments(
            new MpvQualityEnhancementOptions(Interpolation: MpvFrameInterpolationMode.Rife)));
        Assert.Equal(["off", "mpv", "rife"], MpvFrameInterpolationModes.SupportedValues);
        Assert.Contains("rife", MpvFrameInterpolationModes.SupportedValues);
        Assert.Empty(MpvFrameInterpolationModes.ReservedValues);
        Assert.Equal(MpvFrameInterpolationMode.Rife, MpvFrameInterpolationModes.Parse("rife"));
    }

    [Fact]
    public void Anime4KPresetFileListsMatchOfficialFastChains()
    {
        Assert.Equal(
        [
            "Anime4K_Clamp_Highlights.glsl",
            "Anime4K_Restore_CNN_M.glsl",
            "Anime4K_Upscale_CNN_x2_M.glsl",
            "Anime4K_AutoDownscalePre_x2.glsl",
            "Anime4K_AutoDownscalePre_x4.glsl",
            "Anime4K_Upscale_CNN_x2_S.glsl",
        ], MpvAnime4KShaderCatalog.FilesFor(MpvAnime4KPreset.A));

        Assert.Equal(
        [
            "Anime4K_Clamp_Highlights.glsl",
            "Anime4K_Restore_CNN_M.glsl",
            "Anime4K_Upscale_CNN_x2_M.glsl",
            "Anime4K_Restore_CNN_S.glsl",
            "Anime4K_AutoDownscalePre_x2.glsl",
            "Anime4K_AutoDownscalePre_x4.glsl",
            "Anime4K_Upscale_CNN_x2_S.glsl",
        ], MpvAnime4KShaderCatalog.FilesFor(MpvAnime4KPreset.APlusA));

        Assert.Equal(
        [
            "Anime4K_Clamp_Highlights.glsl",
            "Anime4K_Restore_CNN_Soft_M.glsl",
            "Anime4K_Upscale_CNN_x2_M.glsl",
            "Anime4K_AutoDownscalePre_x2.glsl",
            "Anime4K_AutoDownscalePre_x4.glsl",
            "Anime4K_Upscale_CNN_x2_S.glsl",
        ], MpvAnime4KShaderCatalog.FilesFor(MpvAnime4KPreset.B));

        Assert.Equal(
        [
            "Anime4K_Clamp_Highlights.glsl",
            "Anime4K_Upscale_Denoise_CNN_x2_M.glsl",
            "Anime4K_AutoDownscalePre_x2.glsl",
            "Anime4K_AutoDownscalePre_x4.glsl",
            "Anime4K_Upscale_CNN_x2_S.glsl",
        ], MpvAnime4KShaderCatalog.FilesFor(MpvAnime4KPreset.C));

        Assert.Empty(MpvAnime4KShaderCatalog.FilesFor(MpvAnime4KPreset.Off));
        Assert.Equal(9, MpvAnime4KShaderCatalog.RequiredFiles.Count);
    }

    [Fact]
    public void ShaderListValueUsesAbsoluteForwardSlashPaths()
    {
        var shaderDirectory = CreateShaderDirectory();
        var options = new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.A, ShaderDirectory: shaderDirectory);
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            options, MpvDisplayPipeline.Auto, SdrVideo(), null, shaderDirectory);

        Assert.Equal(MpvQualityShaderDecision.Apply, plan.Decision);
        var value = MpvAnime4KShaderCatalog.BuildShaderListValue(plan.ShaderPaths);
        Assert.DoesNotContain('\\', value);
        Assert.Equal(plan.ShaderPaths.Count - 1, value.Count(character => character == ';'));
        Assert.All(value.Split(';'), path =>
        {
            Assert.True(Path.IsPathFullyQualified(path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.EndsWith(".glsl", path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SdrContentWithAssetsAppliesShaders()
    {
        var shaderDirectory = CreateShaderDirectory();
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.C),
            MpvDisplayPipeline.Auto,
            SdrVideo(),
            1.0,
            shaderDirectory);

        Assert.Equal(MpvQualityShaderDecision.Apply, plan.Decision);
        Assert.Equal(5, plan.ShaderPaths.Count);
        Assert.Null(plan.DegradedReasonKey);
    }

    [Fact]
    public void HdrContentSkipsAnime4KShaders()
    {
        var shaderDirectory = CreateShaderDirectory();
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.A),
            MpvDisplayPipeline.Auto,
            new MpvVideoTrackInfo("hevc", "p010", "bt.2020", "pq", 3840, 2160),
            4.0,
            shaderDirectory);

        Assert.Equal(MpvQualityShaderDecision.SkipHdr, plan.Decision);
        Assert.Empty(plan.ShaderPaths);
        Assert.Equal(MpvQualityDegradedReasons.HdrContent, plan.DegradedReasonKey);
    }

    [Fact]
    public void SignalPeakAboveOneMarksHdrEvenWhenTransferLooksSdr()
    {
        var shaderDirectory = CreateShaderDirectory();
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.A),
            MpvDisplayPipeline.Auto,
            SdrVideo(),
            2.5,
            shaderDirectory);

        Assert.Equal(MpvQualityShaderDecision.SkipHdr, plan.Decision);
    }

    [Fact]
    public void HdrPassthroughPipelineSkipsAnime4KShaders()
    {
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.A),
            MpvDisplayPipeline.HdrPassthrough,
            SdrVideo(),
            null,
            CreateShaderDirectory());

        Assert.Equal(MpvQualityShaderDecision.SkipHdr, plan.Decision);
        Assert.Equal(MpvQualityDegradedReasons.HdrContent, plan.DegradedReasonKey);
    }

    [Fact]
    public void MissingShaderAssetsSkipAnime4KShaders()
    {
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.B),
            MpvDisplayPipeline.Auto,
            SdrVideo(),
            null,
            Path.Combine(_directory, "does-not-exist"));

        Assert.Equal(MpvQualityShaderDecision.SkipAssets, plan.Decision);
        Assert.Empty(plan.ShaderPaths);
        Assert.Equal(MpvQualityDegradedReasons.AssetsMissing, plan.DegradedReasonKey);
        Assert.Equal(6, plan.MissingFiles!.Count);

        Assert.False(MpvAnime4KShaderCatalog.IsAvailable(Path.Combine(_directory, "does-not-exist")));
        Assert.True(MpvAnime4KShaderCatalog.IsAvailable(CreateShaderDirectory()));
    }

    [Fact]
    public void UnknownContentIsNotReadyAndOffPresetIsNotRequested()
    {
        var shaderDirectory = CreateShaderDirectory();
        Assert.Equal(
            MpvQualityShaderDecision.NotReady,
            MpvQualityEnhancementPlanner.PlanShaders(
                new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.A), MpvDisplayPipeline.Auto, null, null, shaderDirectory).Decision);

        Assert.Equal(
            MpvQualityShaderDecision.NotRequested,
            MpvQualityEnhancementPlanner.PlanShaders(
                new MpvQualityEnhancementOptions(Anime4K: MpvAnime4KPreset.Off), MpvDisplayPipeline.Auto, SdrVideo(), null, shaderDirectory).Decision);
    }

    [Fact]
    public void StatusReportsAppliedFilesAndDegradedReasons()
    {
        var shaderDirectory = CreateShaderDirectory();
        var options = new MpvQualityEnhancementOptions(
            MpvUpscaleQualityPreset.High,
            MpvAnime4KPreset.A,
            Interpolation: MpvFrameInterpolationMode.MpvBuiltIn,
            ShaderDirectory: shaderDirectory);
        var plan = MpvQualityEnhancementPlanner.PlanShaders(
            options, MpvDisplayPipeline.Auto, SdrVideo(), null, shaderDirectory);
        var status = MpvQualityEnhancementPlanner.BuildStatus(options, plan);

        Assert.Equal("high", status.UpscalePreset);
        Assert.Equal("a", status.Anime4KPreset);
        Assert.Equal("mpv", status.FrameInterpolationMode);
        Assert.Equal(MpvQualityShaderStates.Applied, status.ShaderState);
        Assert.Equal(6, status.AppliedShaderFiles.Count);
        Assert.Null(status.DegradedReasonKey);

        var hdrPlan = MpvQualityEnhancementPlanner.PlanShaders(
            options, MpvDisplayPipeline.HdrPassthrough, SdrVideo(), null, shaderDirectory);
        var hdrStatus = MpvQualityEnhancementPlanner.BuildStatus(options, hdrPlan);
        Assert.Equal(MpvQualityShaderStates.SkippedHdr, hdrStatus.ShaderState);
        Assert.Equal(MpvQualityDegradedReasons.HdrContent, hdrStatus.DegradedReasonKey);
    }

    [Fact]
    public void QualitySettingsDefaultToOffAndNormalizePersistedValues()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new AppSettingsStore(path);
        var defaults = store.Load();
        Assert.Equal("off", defaults.MpvUpscalePreset);
        Assert.Equal("off", defaults.MpvAnime4KPreset);
        Assert.Equal("off", defaults.FrameInterpolationMode);

        File.WriteAllText(path,
            "{\"MpvUpscalePreset\":\"ULTRA\",\"MpvAnime4KPreset\":\"A+A\",\"FrameInterpolationMode\":\"MPV\"}");
        var normalized = store.Load();
        Assert.Equal("off", normalized.MpvUpscalePreset);
        Assert.Equal("a_plus_a", normalized.MpvAnime4KPreset);
        Assert.Equal("mpv", normalized.FrameInterpolationMode);

        // RIFE is a first-class mode; its tuning sub-settings default to 2x/4.6/2/auto.
        File.WriteAllText(path, "{\"FrameInterpolationMode\":\"rife\"}");
        var rife = store.Load();
        Assert.Equal("rife", rife.FrameInterpolationMode);
        Assert.Equal(RifeEnhancementSettings.Default, rife.RifeEnhancement);
        File.WriteAllText(path, "{\"FrameInterpolationMode\":\"rife\",\"RifeEnhancement\":{\"Factor\":9,\"Model\":\"bogus\",\"GpuThread\":9,\"ProcessingHeight\":\"2160\"}}");
        Assert.Equal(RifeEnhancementSettings.Default, store.Load().RifeEnhancement);

        // Legacy boolean field from the first v1 iteration migrates to the mpv mode.
        File.WriteAllText(path, "{\"MpvInterpolationEnabled\":true}");
        Assert.Equal("mpv", store.Load().FrameInterpolationMode);

        File.WriteAllText(path, "{\"AudioBalance\":\"high\"}");
        var legacy = store.Load();
        Assert.Equal("high", legacy.AudioBalance);
        Assert.Equal("off", legacy.MpvUpscalePreset);
        Assert.Equal("off", legacy.MpvAnime4KPreset);
        Assert.Equal("off", legacy.FrameInterpolationMode);
    }

    [Fact]
    public void QualitySettingsRoundTripThroughJson()
    {
        var path = Path.Combine(_directory, "roundtrip.json");
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings(
            MpvUpscalePreset: "medium",
            MpvAnime4KPreset: "b",
            FrameInterpolationMode: "mpv"));

        var loaded = store.Load();
        Assert.Equal("medium", loaded.MpvUpscalePreset);
        Assert.Equal("b", loaded.MpvAnime4KPreset);
        Assert.Equal("mpv", loaded.FrameInterpolationMode);
    }

    [Fact]
    public void FromSettingsMapsStoredQualityValues()
    {
        var settings = new AppSettings(
            MpvUpscalePreset: "high",
            MpvAnime4KPreset: "c",
            FrameInterpolationMode: "mpv");
        var options = MpvWindowsVideoOptionMapper.FromSettings(settings, CreateShaderDirectory()).Quality;

        Assert.NotNull(options);
        Assert.Equal(MpvUpscaleQualityPreset.High, options!.Upscale);
        Assert.Equal(MpvAnime4KPreset.C, options.Anime4K);
        Assert.Equal(MpvFrameInterpolationMode.MpvBuiltIn, options.Interpolation);

        var rife = MpvWindowsVideoOptionMapper.FromSettings(
            settings with { FrameInterpolationMode = "rife" }, CreateShaderDirectory()).Quality;
        Assert.Equal(MpvFrameInterpolationMode.Rife, rife!.Interpolation);
    }

    [Fact]
    public async Task PlaybackSettingsApiExposesAndPersistsQualityPresets()
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
        Assert.Equal("off", initial.GetProperty("upscalePreset").GetString());
        Assert.Equal("off", initial.GetProperty("anime4kPreset").GetString());
        Assert.Equal("off", initial.GetProperty("frameInterpolationMode").GetString());
        Assert.Equal(
            ["off", "mpv", "rife"],
            initial.GetProperty("frameInterpolationModeOptions").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.False(initial.GetProperty("rifeEnhancementStatus").GetProperty("installed").GetBoolean());
        Assert.Equal(2, initial.GetProperty("rifeEnhancement").GetProperty("factor").GetInt32());
        Assert.Equal(
            ["off", "low", "medium", "high"],
            initial.GetProperty("upscalePresetOptions").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["off", "a", "a_plus_a", "b", "c"],
            initial.GetProperty("anime4kPresetOptions").EnumerateArray().Select(item => item.GetString()!).ToArray());

        var updateResponse = await client.PutAsJsonAsync("/api/settings/playback", new
        {
            upscalePreset = "high",
            anime4kPreset = "a_plus_a",
            frameInterpolationMode = "mpv",
        });
        updateResponse.EnsureSuccessStatusCode();
        var updated = await ReadData(updateResponse);
        Assert.Equal("high", updated.GetProperty("upscalePreset").GetString());
        Assert.Equal("a_plus_a", updated.GetProperty("anime4kPreset").GetString());
        Assert.Equal("mpv", updated.GetProperty("frameInterpolationMode").GetString());
        Assert.Equal("high", settings.MpvUpscalePreset);
        Assert.Equal("a_plus_a", settings.MpvAnime4KPreset);
        Assert.Equal("mpv", settings.FrameInterpolationMode);
        Assert.Equal("mpv", applied?.FrameInterpolationMode);

        var invalidUpscale = await client.PutAsJsonAsync("/api/settings/playback", new { upscalePreset = "ultra" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidUpscale.StatusCode);
        var invalidAnime4K = await client.PutAsJsonAsync("/api/settings/playback", new { anime4kPreset = "z" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidAnime4K.StatusCode);
        var invalidInterpolation = await client.PutAsJsonAsync("/api/settings/playback", new { frameInterpolationMode = "bogus" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidInterpolation.StatusCode);
        var rifeResponse = await client.PutAsJsonAsync("/api/settings/playback", new
        {
            frameInterpolationMode = "rife",
            rifeEnhancement = new { factor = 3, model = "4.25-lite", gpuThread = 4, processingHeight = "576" },
        });
        rifeResponse.EnsureSuccessStatusCode();
        var rifeUpdated = await ReadData(rifeResponse);
        Assert.Equal("rife", rifeUpdated.GetProperty("frameInterpolationMode").GetString());
        Assert.Equal(3, rifeUpdated.GetProperty("rifeEnhancement").GetProperty("factor").GetInt32());
        Assert.Equal("4.25-lite", rifeUpdated.GetProperty("rifeEnhancement").GetProperty("model").GetString());
        Assert.Equal(4, rifeUpdated.GetProperty("rifeEnhancement").GetProperty("gpuThread").GetInt32());
        Assert.Equal("576", rifeUpdated.GetProperty("rifeEnhancement").GetProperty("processingHeight").GetString());
        Assert.Equal(3, settings.RifeEnhancement?.Factor);

        var invalidRife = await client.PutAsJsonAsync("/api/settings/playback", new
        {
            rifeEnhancement = new { factor = 7, model = "4.6", gpuThread = 2, processingHeight = "auto" },
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidRife.StatusCode);
    }

    private static MpvVideoTrackInfo SdrVideo() => new("h264", "yuv420p", "bt.709", "bt.1886", 1920, 1080);

    private string CreateShaderDirectory()
    {
        var directory = Path.Combine(_directory, "shaders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in MpvAnime4KShaderCatalog.RequiredFiles)
            File.WriteAllText(Path.Combine(directory, file), "// test shader");
        return directory;
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
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
