using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class SdlGamepadBackendTests : IDisposable
{
    private static readonly string[] ExpectedBackends = ["SDL3", "XInput", "none", "demo"];
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "miruplay-gamepad-diagnostics-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousLogDirectory = TvGamepadDiagnostics.LogDirectoryOverride;

    public SdlGamepadBackendTests()
    {
        TvGamepadDiagnostics.LogDirectoryOverride = _logDirectory;
    }

    public void Dispose()
    {
        TvGamepadDiagnostics.LogDirectoryOverride = _previousLogDirectory;
        try { Directory.Delete(_logDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [Fact]
    public void SdlSourceSmokeDoesNotThrowWithoutControllerOrDll()
    {
        using var source = new SdlGamepadSource();
        var connected = source.TryGetState(0, out var state);
        // Environment dependent: either the DLL is absent (false) or it is
        // loaded with no controller attached. Both must be safe.
        Assert.False(connected && !state.Connected);
        Assert.False(source.TryGetState(1, out _));
    }

    [Fact]
    public void XInputSourceRejectsInvalidControllerIndexes()
    {
        var source = new XInputSource();
        Assert.False(source.TryGetState(-1, out _));
        Assert.False(source.TryGetState(4, out _));
    }

    [Theory]
    [InlineData(null, "xbox")]
    [InlineData("", "xbox")]
    [InlineData("xbox", "xbox")]
    [InlineData("Nintendo", "nintendo")]
    [InlineData("unknown", "xbox")]
    public void FaceLayoutIdsAreNormalized(string? value, string expected) =>
        Assert.Equal(expected, TvGamepadFaceLayouts.Normalize(value));

    [Fact]
    public void FaceLayoutParsesToEnum()
    {
        Assert.Equal(TvGamepadFaceLayout.Nintendo, TvGamepadFaceLayouts.Parse("nintendo"));
        Assert.Equal(TvGamepadFaceLayout.Xbox, TvGamepadFaceLayouts.Parse("xbox"));
    }

    [Fact]
    public void AutoSourceStatusReportsBackendStateWithoutThrowing()
    {
        using var source = new AutoGamepadSource();
        var status = source.GetStatus();
        Assert.False(status.Demo);
        Assert.False(status.Connected);
        Assert.Contains(status.ActiveBackend, ExpectedBackends);
    }

    [Fact]
    public void Sdl3InitFailureIsExposedAsDiagnosticWarning()
    {
        var message = "unit-test init failure " + Guid.NewGuid().ToString("N");
        TvGamepadDiagnostics.ReportSdl3InitFailure(message);
        Assert.Equal(message, TvGamepadDiagnostics.Sdl3InitError);
        Assert.Contains(message, TvGamepadDiagnostics.LastWarning);
        Assert.Contains("XInput", TvGamepadDiagnostics.LastWarning);

        var logPath = Path.Combine(_logDirectory, "tv-gamepad.log");
        Assert.True(File.Exists(logPath), "SDL3 init failure should be written to the injected log directory.");
        var log = File.ReadAllText(logPath);
        Assert.Contains("[WARN]", log);
        Assert.Contains(message, log);
    }
}
