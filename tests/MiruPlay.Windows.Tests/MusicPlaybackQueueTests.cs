using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MusicPlaybackQueueTests
{
    private static LibraryEpisode Track(string key, string title) => new(
        0, $"music-{key}", $"music:7:album:{key}", 1, 1, 1, title, $"C:\\music\\{key}.flac", TimeSpan.Zero, []);

    [Fact]
    public void QueueAdvancesThroughAlbumTracksInOrder()
    {
        var tracks = new[] { Track("a", "A"), Track("b", "B"), Track("c", "C") };
        var queue = new MusicPlaybackQueue(tracks, tracks[0].ProgressKey);

        Assert.Equal("A", queue.Current?.Title);
        Assert.Equal("B", queue.Next()?.Title);
        Assert.Equal("C", queue.Next()?.Title);
        Assert.Null(queue.Next());
    }

    [Fact]
    public void UnknownStartKeyDisablesAdvance()
    {
        var queue = new MusicPlaybackQueue([Track("a", "A")], "music:7:album:missing");

        Assert.Null(queue.Current);
        Assert.Null(queue.Next());
    }

    [Fact]
    public void ResetToRepositionsWithinTheSameAlbum()
    {
        var tracks = new[] { Track("a", "A"), Track("b", "B") };
        var queue = new MusicPlaybackQueue(tracks, tracks[1].ProgressKey);

        Assert.Null(queue.Next());
        queue.ResetTo(tracks[0].ProgressKey);
        Assert.Equal("B", queue.Next()?.Title);
    }
}
