using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AudioDspRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-dsp-{Guid.NewGuid():N}");

    public AudioDspRuntimeTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MissingAudioDspFieldLoadsNeutralConfiguration()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"LibraryRoot\":null}");

        var settings = new AppSettingsStore(path).Load();

        Assert.False(settings.AudioDsp!.Enabled);
        Assert.Equal(AudioDspConfig.DefaultPresetId, settings.AudioDsp.SelectedPresetId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
