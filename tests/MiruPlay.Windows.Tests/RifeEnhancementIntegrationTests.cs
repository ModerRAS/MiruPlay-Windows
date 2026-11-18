using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

/// <summary>Real-machine validation for the optional RIFE enhancement pack. These tests only run
/// when the maintainer supplies the pinned libmpv library and the staged/archived enhancement pack;
/// CI keeps them inert unless the MIRUPLAY_REQUIRE_RIFE_SMOKE guard is set.</summary>
[CollectionDefinition("MpvIntegration", DisableParallelization = true)]
public sealed class MpvIntegrationGroup;

[Collection("MpvIntegration")]
public sealed class RifeEnhancementIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miruplay-rife-it-" + Guid.NewGuid().ToString("N"));

    public RifeEnhancementIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StagedPackInstallsRunsSelftestAndUninstalls()
    {
        var archive = Environment.GetEnvironmentVariable("MIRUPLAY_RIFE_PACK_ARCHIVE");
        if (archive is null || !File.Exists(archive))
        {
            Assert.False(Environment.GetEnvironmentVariable("MIRUPLAY_REQUIRE_RIFE_SMOKE") == "1",
                "MIRUPLAY_RIFE_PACK_ARCHIVE is required for the RIFE enhancement smoke.");
            return;
        }

        using var service = new RifeEnhancementService(
            installedRoot: Path.Combine(_directory, "installed"),
            bundledRoot: Path.Combine(_directory, "bundled"),
            stateStore: new RifeEnhancementStateStore(Path.Combine(_directory, "state.json")),
            scriptDirectory: Path.Combine(_directory, "scripts"),
            mirrorRoot: Path.Combine(_directory, "mirror"));
        Assert.NotNull(service.Manifest);
        Assert.DoesNotContain("example.invalid", service.Manifest!.SourceArchive.Url, StringComparison.OrdinalIgnoreCase);

        var installed = await service.InstallFromArchiveAsync(archive);
        Assert.True(installed.Installed, installed.ReasonKey);
        Assert.Equal(service.Manifest.Version, installed.Version);
        Assert.True(installed.Ready, installed.SelfTest?.ErrorKey ?? installed.ReasonKey);
        Assert.NotNull(installed.SelfTest?.OutputFps480p);
        Assert.True(installed.SelfTest!.OutputFps480p >= 30,
            $"Self-test fps too low: {installed.SelfTest.OutputFps480p:0.0}");
        Assert.True(installed.SelfTest.RecommendedHeight is "360" or "480" or "540" or "576" or "720" or "1080");

        var removed = await service.UninstallAsync();
        Assert.False(removed.Installed);
        Assert.False(Directory.Exists(service.InstalledRoot));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealLibMpvAppliesRifeFrameInterpolationFromStagedPack()
    {
        var libMpvPath = Environment.GetEnvironmentVariable("MIRUPLAY_LIBMPV_PATH");
        var packRoot = Environment.GetEnvironmentVariable("MIRUPLAY_RIFE_PACK_ROOT");
        var mediaPath = Environment.GetEnvironmentVariable("MIRUPLAY_RIFE_SMOKE_MEDIA");
        if (libMpvPath is null || packRoot is null || mediaPath is null ||
            !File.Exists(libMpvPath) || !File.Exists(mediaPath) ||
            !File.Exists(Path.Combine(packRoot, "manifest.json")))
        {
            Assert.False(Environment.GetEnvironmentVariable("MIRUPLAY_REQUIRE_RIFE_SMOKE") == "1",
                "MIRUPLAY_LIBMPV_PATH, MIRUPLAY_RIFE_PACK_ROOT and MIRUPLAY_RIFE_SMOKE_MEDIA are required.");
            return;
        }

        var packVersion = JsonDocument.Parse(File.ReadAllText(Path.Combine(packRoot, "manifest.json")))
            .RootElement.GetProperty("version").GetString();
        var state = new RifeEnhancementStateStore(Path.Combine(_directory, "playback-state.json"));
        state.Save(new RifeEnhancementState(
            InstalledVersion: packVersion,
            SelfTest: new RifeSelfTestResult(
                true, 70, "integration", null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), packVersion, "480")));
        using var service = new RifeEnhancementService(
            installedRoot: Path.Combine(_directory, "unused-installed"),
            bundledRoot: packRoot,
            stateStore: state,
            scriptDirectory: Path.Combine(_directory, "scripts"),
            mirrorRoot: Path.Combine(_directory, "mirror"));
        var settings = new AppSettings(LibMpvPath: libMpvPath, FrameInterpolationMode: "rife");
        var request = service.ResolvePlaybackRequest(settings, MpvDisplayPipeline.Auto);
        Assert.NotNull(request);
        Assert.NotNull(request!.Plan);

        var media = Path.Combine(_directory, "rife-smoke.mp4");
        File.Copy(mediaPath, media);
        var episode = new LibraryEpisode(
            1, "rife-episode", "rife-progress", 1, 1, 1, "RIFE smoke", media, TimeSpan.FromSeconds(20), []);
        var window = TestWindow.Create();
        try
        {
            await using var session = await LibMpvPlaybackSession.StartAsync(
                libMpvPath,
                episode,
                settings,
                new PlaybackProgressStore(Path.Combine(_directory, "state.db")),
                headless: false,
                windowHandle: window,
                transportLease: null,
                videoOptions: MpvWindowsVideoOptionMapper.FromSettings(settings, rife: request));

            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.True(session.IsActive, session.LastError);
            var quality = session.QualityEnhancement;
            Assert.NotNull(quality);
            Assert.Equal(MpvFrameInterpolationModes.Rife, quality!.FrameInterpolationMode);
            Assert.Equal(RifeEnhancementStates.Applied, quality.RifeState);
            Assert.Null(quality.RifeDegradedReasonKey);
            Assert.Equal(2, quality.RifeFactor);

            var diagnostics = session.GetNativeDiagnostics();
            var properties = diagnostics.Properties.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
            if (properties.TryGetValue("estimated-vf-fps", out var estimated) &&
                double.TryParse(estimated, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps))
            {
                Assert.True(fps >= 44, $"Expected RIFE output near 48 fps, got {fps:0.00}.");
            }
            if (properties.TryGetValue("frame-drop-count", out var drops) &&
                int.TryParse(drops, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dropCount))
            {
                Assert.True(dropCount <= 2, $"Unexpected frame drops with RIFE 480p: {dropCount}.");
            }
            Assert.Contains("vapoursynth", properties.GetValueOrDefault("vf") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestWindow.Destroy(window);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static class TestWindow
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        public static IntPtr Create() =>
            CreateWindowEx(0, "STATIC", "rife-integration", 0x80000000u, 0, 0, 640, 360, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        public static void Destroy(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        }
    }
}