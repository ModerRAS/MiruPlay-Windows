using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AutoUpdateSettingsTests
{
    [Fact]
    public void DefaultsAreDownloadAndSixHours()
    {
        var settings = new AppSettings();
        Assert.Equal("download", settings.AutoUpdateMode);
        Assert.Equal(6, settings.AutoUpdateIntervalHours);
    }

    [Fact]
    public async Task LegacySettingsWithoutAutoUpdateFieldsLoadDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"AppLanguage\":\"zh-CN\",\"WebControlEnabled\":true}");
            var store = new AppSettingsStore(path);
            var settings = store.Load();
            Assert.Equal("download", settings.AutoUpdateMode);
            Assert.Equal(6, settings.AutoUpdateIntervalHours);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InvalidAutoUpdateModeFallsBackToDownloadAndIntervalClamps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"AutoUpdateMode\":\"nonsense\",\"AutoUpdateIntervalHours\":999}");
            var store = new AppSettingsStore(path);
            var settings = store.Load();
            Assert.Equal("download", settings.AutoUpdateMode);
            Assert.Equal(168, settings.AutoUpdateIntervalHours);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
