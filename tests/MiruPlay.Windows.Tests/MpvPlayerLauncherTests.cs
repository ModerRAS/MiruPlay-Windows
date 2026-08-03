using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvPlayerLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-mpv-{Guid.NewGuid():N}");

    public MpvPlayerLauncherTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CreateStartInfoRestoresProgressAndAddsSubtitles()
    {
        var mediaPath = CreateFile("episode.mkv");
        var subtitlePath = CreateFile("episode.zh-CN.srt");
        var episode = CreateEpisode(mediaPath, subtitlePath);
        var progress = new PlaybackProgress("key", 30_000, 100_000, 1, 0);

        var startInfo = MpvPlayerLauncher.CreateStartInfo(
            "mpv.exe",
            "test-pipe",
            episode,
            new AppSettings(PreferredSubtitleLanguage: "zh_hans"),
            progress);

        Assert.Contains("--input-ipc-server=\\\\.\\pipe\\test-pipe", startInfo.ArgumentList);
        Assert.Contains("--resume-playback=no", startInfo.ArgumentList);
        Assert.Contains("--keep-open=yes", startInfo.ArgumentList);
        Assert.DoesNotContain("--save-position-on-quit=yes", startInfo.ArgumentList);
        Assert.Contains("--start=30", startInfo.ArgumentList);
        Assert.Contains("--slang=zh-Hans,zh-CN,chs,sc,chi,zho", startInfo.ArgumentList);
        Assert.Contains($"--sub-file={subtitlePath}", startInfo.ArgumentList);
        Assert.Equal(mediaPath, startInfo.ArgumentList[^1]);
    }

    [Fact]
    public async Task RemotePlaybackWithoutSharedProxyIsRejected()
    {
        var episode = CreateEpisode(
            "https://example.com/dav/Anime/01.mkv",
            "https://example.com/dav/Anime/01.zh-CN.srt");
        var progress = new PlaybackProgressStore(Path.Combine(_directory, "remote-state.db"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MpvPlayerLauncher.PlayAsync(
            episode,
            new AppSettings(PlayerPath: "missing-mpv.exe"),
            progress));

        Assert.Contains("shared endpoint consumer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferredLanguagePrioritizesMatchingExternalSubtitleWithoutDroppingTracks()
    {
        var paths = new[]
        {
            "episode.en.srt",
            "episode.zh-TW.ass",
            "episode.zh-CN.srt",
            "episode.jpn.ass",
        };

        Assert.Collection(
            MpvPlayerLauncher.PrioritizeSubtitlePaths(paths, "zh_hans"),
            item => Assert.Equal("episode.zh-CN.srt", item),
            item => Assert.Equal("episode.en.srt", item),
            item => Assert.Equal("episode.zh-TW.ass", item),
            item => Assert.Equal("episode.jpn.ass", item));
        Assert.Equal(paths, MpvPlayerLauncher.PrioritizeSubtitlePaths(paths, "auto"));
        var falsePositivePaths = new[] { "neutral.srt", "Engineer.srt", "Chihayafuru.srt" };
        Assert.Equal(falsePositivePaths, MpvPlayerLauncher.PrioritizeSubtitlePaths(falsePositivePaths, "en"));
    }

    [Fact]
    public void CreateStartInfoDoesNotResumeCompletedEpisode()
    {
        var mediaPath = CreateFile("episode.mkv");
        var episode = CreateEpisode(mediaPath);
        var progress = new PlaybackProgress("key", 100_000, 100_000, 1, 1);

        var startInfo = MpvPlayerLauncher.CreateStartInfo(
            "mpv.exe",
            "test-pipe",
            episode,
            new AppSettings(),
            progress);

        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.StartsWith("--start=", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateStartInfoAppliesAssOverrideLastForEveryMpvPlaybackMode()
    {
        var episode = CreateEpisode(CreateFile("episode.mkv"), CreateFile("episode.ass"));

        var embedded = MpvPlayerLauncher.CreateStartInfo("mpv.exe", "embedded-pipe", episode, new AppSettings(), null);
        var headless = MpvPlayerLauncher.CreateStartInfo("mpv.exe", "headless-pipe", episode, new AppSettings(), null, headless: true);

        Assert.All(
            new[] { embedded, headless },
            startInfo => Assert.Equal("--sub-ass-override=strip", startInfo.ArgumentList[^2]));
    }

    private static LibraryEpisode CreateEpisode(string mediaPath, params string[] subtitles) => new(
        1,
        "episode-uuid",
        "episode-key",
        1,
        1,
        1,
        "Episode",
        mediaPath,
        TimeSpan.FromMinutes(24),
        subtitles);

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
