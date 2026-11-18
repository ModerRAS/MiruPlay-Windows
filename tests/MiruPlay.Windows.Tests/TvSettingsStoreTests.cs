using MiruPlay.Windows.Services;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-tv-settings-{Guid.NewGuid():N}");

    public TvSettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void LegacySettingsWithoutTvFieldsLoadWithDefaults()
    {
        File.WriteAllText(SettingsPath, """{"AppLanguage":"zh-CN","MediaSourceSchemaVersion":1,"MediaSources":[]}""");
        var store = new AppSettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.False(settings.TvModeEnabled);
        Assert.Equal(100, settings.TvUiScalePercent);
        Assert.True(settings.TvSoundFeedback);
        Assert.True(settings.TvClockVisible);
        Assert.Equal("general", settings.TvRemotePreset);
        Assert.Equal("system", settings.TvVolumeKeyTarget);
        Assert.Equal(2000, settings.TvExitLongPressMs);
        Assert.True(settings.TvGamepadEnabled);
        Assert.Equal(50, settings.TvGamepadDeadzonePercent);
        Assert.Equal("xbox", settings.TvGamepadFaceLayout);
    }

    [Fact]
    public void InvalidTvValuesAreNormalizedOnLoad()
    {
        File.WriteAllText(SettingsPath, """
            {"MediaSourceSchemaVersion":1,"MediaSources":[],"TvRemotePreset":"KODI","TvUiScalePercent":133,
             "TvExitLongPressMs":50,"TvVolumeKeyTarget":"amp","TvModeEnabled":true}
            """);
        var store = new AppSettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.Equal("general", settings.TvRemotePreset);
        Assert.Equal(100, settings.TvUiScalePercent);
        Assert.Equal(1000, settings.TvExitLongPressMs);
        Assert.Equal("system", settings.TvVolumeKeyTarget);
        Assert.True(settings.TvModeEnabled);
        Assert.Equal(50, settings.TvGamepadDeadzonePercent);
        Assert.Equal("xbox", settings.TvGamepadFaceLayout);
    }

    [Fact]
    public void TvSettingsRoundTrip()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings(
            MediaSourceSchemaVersion: 1,
            MediaSources: [],
            TvModeEnabled: true,
            TvRemotePreset: "mce",
            TvUiScalePercent: 125,
            TvExitLongPressMs: 3000,
            TvGamepadEnabled: false,
            TvGamepadDeadzonePercent: 70,
            TvGamepadFaceLayout: "nintendo",
            TvClockVisible: false,
            TvSoundFeedback: false,
            TvVolumeKeyTarget: "application"));

        var settings = store.Load();

        Assert.True(settings.TvModeEnabled);
        Assert.Equal("mce", settings.TvRemotePreset);
        Assert.Equal(125, settings.TvUiScalePercent);
        Assert.Equal(3000, settings.TvExitLongPressMs);
        Assert.False(settings.TvGamepadEnabled);
        Assert.Equal(70, settings.TvGamepadDeadzonePercent);
        Assert.Equal("nintendo", settings.TvGamepadFaceLayout);
        Assert.False(settings.TvClockVisible);
        Assert.False(settings.TvSoundFeedback);
        Assert.Equal("application", settings.TvVolumeKeyTarget);
    }
}
