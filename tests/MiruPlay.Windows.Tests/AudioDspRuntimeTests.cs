using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AudioDspRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-dsp-{Guid.NewGuid():N}");

    public AudioDspRuntimeTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CreateStartInfoAddsDspArgumentsOnlyWhenEnabled()
    {
        var mediaPath = Path.Combine(_directory, "episode.mkv");
        File.WriteAllText(mediaPath, string.Empty);
        var episode = new LibraryEpisode(
            1,
            "episode-uuid",
            "episode-key",
            1,
            1,
            1,
            "Episode",
            mediaPath,
            TimeSpan.FromMinutes(24),
            []);
        var settings = new AppSettings(AudioDsp: new AudioDspConfig(
            true,
            AudioDspConfig.DefaultPresetId,
            [AudioDspPreset.Neutral()]));

        var startInfo = MpvPlayerLauncher.CreateStartInfo(
            "mpv.exe", "pipe", episode, settings, null);

        Assert.Contains("--audio-format=float", startInfo.ArgumentList);
        Assert.Contains(startInfo.ArgumentList, argument => argument.StartsWith("--af=", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAudioDspFieldLoadsNeutralConfiguration()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"LibraryRoot\":null}");

        var settings = new AppSettingsStore(path).Load();

        Assert.False(settings.AudioDsp!.Enabled);
        Assert.Equal(AudioDspConfig.DefaultPresetId, settings.AudioDsp.SelectedPresetId);
    }

    [Fact]
    public void AudioDspCommandClearsThePrivateMpvFilterChainWhenDisabled()
    {
        var command = MpvPlaybackSession.CreateAudioDspCommand(
            new AudioDspFilterGraph("", [], "disabled", []));

        Assert.Equal("set_property", command[0]);
        Assert.Equal("af", command[1]);
        Assert.Empty((object[])command[2]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
