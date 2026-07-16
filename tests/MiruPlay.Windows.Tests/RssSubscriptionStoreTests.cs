using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RssSubscriptionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-rss-{Guid.NewGuid():N}");
    private string PathName => Path.Combine(_directory, "rss.json");

    [Fact]
    public void AddUpdateRemovePersistsSubscriptions()
    {
        var store = new RssSubscriptionStore(PathName);

        var added = store.Add(new RssSubscriptionRequest(0, "  Anime feed  ", "https://example.test/feed.xml", "Frieren", true));
        var updated = store.Update(added.Id, new RssSubscriptionRequest(999, "Anime", "https://example.test/new.xml", null, false));
        var reloaded = Assert.Single(new RssSubscriptionStore(PathName).List());

        Assert.Equal(1, added.Id);
        Assert.Equal("Anime", updated.Name);
        Assert.Equal("https://example.test/new.xml", reloaded.Url);
        Assert.False(reloaded.Enabled);
        store.MarkChecked(added.Id, 12345);
        Assert.Equal(12345, store.List()[0].LastCheckedAt);
        store.Remove(added.Id);
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData("ftp://example.test/feed.xml", "ok")]
    [InlineData("https://user:secret@example.test/feed.xml", "ok")]
    [InlineData("https://example.test/feed.xml", "[")]
    public void RejectsUnsafeUrlsAndInvalidRegex(string url, string filter)
    {
        var store = new RssSubscriptionStore(PathName);

        Assert.Throws<ArgumentException>(() => store.Add(new RssSubscriptionRequest(0, "Anime", url, filter)));
        Assert.False(File.Exists(PathName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
