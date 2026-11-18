using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackModeSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miruplay-playback-mode-" + Guid.NewGuid().ToString("N"));

    public PlaybackModeSettingsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MissingSettingsFileUsesPreservingDefaults()
    {
        var store = new AppSettingsStore(Path.Combine(_directory, "settings.json"));

        var settings = store.Load();

        Assert.Equal("auto", settings.HdrMode);
        Assert.Equal("off", settings.AudioPassthroughMode);
        Assert.Equal("truehd,eac3,ac3,dts,dts-hd", settings.AudioPassthroughCodecs);
        Assert.Equal("auto", settings.AudioPassthroughDevice);
    }

    [Fact]
    public void PlaybackModeSettingsRoundTripThroughJson()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings(
            HdrMode: "passthrough",
            AudioPassthroughMode: "force",
            AudioPassthroughCodecs: "eac3,truehd",
            AudioPassthroughDevice: "wasapi/{device}"));

        var loaded = store.Load();

        Assert.Equal("passthrough", loaded.HdrMode);
        Assert.Equal("force", loaded.AudioPassthroughMode);
        Assert.Equal("truehd,eac3", loaded.AudioPassthroughCodecs);
        Assert.Equal("wasapi/{device}", loaded.AudioPassthroughDevice);
    }

    [Fact]
    public void InvalidValuesAreNormalizedOnLoad()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path,
            "{\"HdrMode\":\"BOGUS\",\"AudioPassthroughMode\":\"ON\",\"AudioPassthroughCodecs\":\"truehd,bogus,truehd\",\"AudioPassthroughDevice\":\"Speakers\"}");

        var settings = new AppSettingsStore(path).Load();

        Assert.Equal("auto", settings.HdrMode);
        Assert.Equal("off", settings.AudioPassthroughMode);
        Assert.Equal("truehd", settings.AudioPassthroughCodecs);
        Assert.Equal("auto", settings.AudioPassthroughDevice);
    }

    [Fact]
    public void LegacyJsonWithoutPlaybackModeFieldsLoadsDefaults()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"AudioBalance\":\"high\",\"AudioSrcBypass\":\"system\"}");

        var settings = new AppSettingsStore(path).Load();

        Assert.Equal("high", settings.AudioBalance);
        Assert.Equal("auto", settings.HdrMode);
        Assert.Equal("off", settings.AudioPassthroughMode);
        Assert.Equal("truehd,eac3,ac3,dts,dts-hd", settings.AudioPassthroughCodecs);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("PASSTHROUGH", "passthrough")]
    [InlineData("sdr_tonemap", "sdr_tonemap")]
    [InlineData("bogus", "auto")]
    public void HdrModeNormalizerHandlesStorageValues(string? value, string expected) =>
        Assert.Equal(expected, AppSettingsStore.NormalizeHdrMode(value));

    [Theory]
    [InlineData(null, "off")]
    [InlineData("AUTO", "auto")]
    [InlineData("force", "force")]
    [InlineData("bogus", "off")]
    public void PassthroughModeNormalizerHandlesStorageValues(string? value, string expected) =>
        Assert.Equal(expected, AppSettingsStore.NormalizeAudioPassthroughMode(value));

    [Theory]
    [InlineData(null, "truehd,eac3,ac3,dts,dts-hd")]
    [InlineData("", "truehd,eac3,ac3,dts,dts-hd")]
    [InlineData("eac3,truehd", "truehd,eac3")]
    [InlineData("truehd,truehd,bogus", "truehd")]
    public void PassthroughCodecNormalizerFiltersAndOrders(string? value, string expected) =>
        Assert.Equal(expected, AppSettingsStore.NormalizeAudioPassthroughCodecs(value));

    [Theory]
    [InlineData(null, "auto")]
    [InlineData(" auto ", "auto")]
    [InlineData("wasapi/{device}", "wasapi/{device}")]
    [InlineData("Speakers", "auto")]
    public void PassthroughDeviceNormalizerRequiresWasapiEndpoints(string? value, string expected) =>
        Assert.Equal(expected, AppSettingsStore.NormalizeAudioPassthroughDevice(value));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
