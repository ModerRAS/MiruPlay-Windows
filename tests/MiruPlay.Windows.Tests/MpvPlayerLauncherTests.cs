using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvPlayerLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-mpv-{Guid.NewGuid():N}");

    public MpvPlayerLauncherTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FindLibMpvUsesAnExistingConfiguredLibraryPath()
    {
        var path = Path.Combine(_directory, "libmpv-2.dll");
        File.WriteAllBytes(path, []);

        Assert.Equal(path, MpvPlayerLauncher.FindLibMpv(path));
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
            new AppSettings(),
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
    public void ReleaseScriptOnlyReferencesTheInProcessLibMpvRuntime()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "Publish-Release.ps1"));

        Assert.Contains("libmpv-2.dll", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-MpvRuntime", script, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime\\mpv", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$mpvPath", script, StringComparison.Ordinal);
    }

    private static LibraryEpisode CreateEpisode(params string[] subtitles) => new(
        1,
        "episode-uuid",
        "episode-key",
        1,
        1,
        1,
        "Episode",
        "https://example.com/dav/Anime/01.mkv",
        TimeSpan.FromMinutes(24),
        subtitles);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "Publish-Release.ps1")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
