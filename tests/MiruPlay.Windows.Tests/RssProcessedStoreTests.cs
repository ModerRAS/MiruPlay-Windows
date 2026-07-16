using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RssProcessedStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-rss-state-{Guid.NewGuid():N}");

    [Fact]
    public void ProcessedItemsPersistWithSubscriptionScopedKeys()
    {
        var path = Path.Combine(_directory, "rss-state.db");
        var store = new RssProcessedStore(path);

        Assert.False(store.IsProcessed(1, "episode-1"));
        store.MarkProcessed(new RssProcessedItem(1, "episode-1", "Episode 1", "magnet:?xt=urn:btih:a", 123));

        var reloaded = new RssProcessedStore(path);
        Assert.True(reloaded.IsProcessed(1, "episode-1"));
        Assert.False(reloaded.IsProcessed(2, "episode-1"));
        Assert.Equal(1, reloaded.Count(1));
        reloaded.MarkProcessed(new RssProcessedItem(1, "episode-1", "Episode 1 updated", "magnet:?xt=urn:btih:a", 456));
        Assert.Equal(1, reloaded.Count(1));
    }

    [Fact]
    public void SubmittedItemAndDownloadTaskPersistAtomicallyWithoutDuplicates()
    {
        var path = Path.Combine(_directory, "submitted-state.db");
        var store = new RssProcessedStore(path);

        store.MarkSubmitted(new RssProcessedItem(7, "episode-7", "Episode 7", "magnet:?xt=urn:btih:seven", 123));
        store.MarkSubmitted(new RssProcessedItem(7, "episode-7", "Episode 7 updated", "magnet:?xt=urn:btih:seven", 456));

        var reloaded = new RssProcessedStore(path);
        Assert.True(reloaded.IsProcessed(7, "episode-7"));
        var task = Assert.Single(reloaded.ListDownloadTasks());
        Assert.Equal(7, task.SubscriptionId);
        Assert.Equal("episode-7", task.ItemKey);
        Assert.Equal("Episode 7 updated", task.Title);
        Assert.Equal("SUBMITTED", task.Status);
        Assert.Null(task.Message);
        Assert.Equal(123, task.CreatedAt);
        Assert.Equal(456, task.UpdatedAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
