using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackProgressStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-progress-{Guid.NewGuid():N}");
    private readonly PlaybackProgressStore _store;

    public PlaybackProgressStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new PlaybackProgressStore(Path.Combine(_directory, "state.db"));
    }

    [Fact]
    public void SavePersistsAndClampsProgress()
    {
        _store.Save("episode-1", positionMs: 120_000, durationMs: 100_000);

        var progress = Assert.IsType<PlaybackProgress>(_store.Get("episode-1"));

        Assert.Equal(100_000, progress.PositionMs);
        Assert.Equal(100_000, progress.DurationMs);
        Assert.True(progress.IsCompleted);
        Assert.Equal(0, progress.PlayCount);
    }

    [Fact]
    public void CompletedSaveIncrementsPlayCountAndPreservesKnownDuration()
    {
        _store.Save("episode-1", positionMs: 20_000, durationMs: 100_000);
        _store.Save("episode-1", positionMs: 0, durationMs: 0, completed: true);

        var progress = Assert.IsType<PlaybackProgress>(_store.Get("episode-1"));

        Assert.Equal(100_000, progress.PositionMs);
        Assert.Equal(100_000, progress.DurationMs);
        Assert.Equal(1, progress.PlayCount);
        Assert.True(progress.IsCompleted);
    }

    [Fact]
    public void MlipEpisodeKeyIsStableAndSourceScoped()
    {
        var first = PlaybackProgressStore.BuildMlipEpisodeKey(Path.Combine(_directory, "Library"), "episode-uuid");
        var same = PlaybackProgressStore.BuildMlipEpisodeKey(Path.Combine(_directory, "library"), "episode-uuid");
        var other = PlaybackProgressStore.BuildMlipEpisodeKey(Path.Combine(_directory, "Other"), "episode-uuid");

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);

        var remote = PlaybackProgressStore.BuildMlipEpisodeKey("https://example.com/dav", "episode-uuid");
        var sameRemote = PlaybackProgressStore.BuildMlipEpisodeKey("https://EXAMPLE.com/dav/", "episode-uuid");
        var otherRemote = PlaybackProgressStore.BuildMlipEpisodeKey("https://example.com/other", "episode-uuid");
        var caseDistinctRemote = PlaybackProgressStore.BuildMlipEpisodeKey("https://example.com/DAV", "episode-uuid");
        Assert.Equal(remote, sameRemote);
        Assert.NotEqual(remote, otherRemote);
        Assert.NotEqual(remote, caseDistinctRemote);
        Assert.NotEqual(first, remote);

        var smb = PlaybackProgressStore.BuildMlipEpisodeKey("smb://Server/Share/Anime", "episode-uuid");
        var sameSmb = PlaybackProgressStore.BuildMlipEpisodeKey("\\\\server\\share\\anime", "episode-uuid");
        Assert.Equal(smb, sameSmb);
        Assert.NotEqual(smb, PlaybackProgressStore.BuildMlipEpisodeKey("smb://server/other/anime", "episode-uuid"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
