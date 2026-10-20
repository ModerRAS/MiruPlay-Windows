using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RssFeedClientTests
{
    [Fact]
    public async Task ParsesMediaFeedMetadataAndBuildsVirtualEpisodeCatalog()
    {
        var client = new RssFeedClient(new TextHandler("""
            <rss version="2.0"><channel>
              <item><title>[Group] Frieren - 02 [1080p].mkv</title><guid>frieren-2</guid><category>frieren-season-1</category><pubDate>Sun, 18 Oct 2026 10:00:00 GMT</pubDate><size>1.5 MB</size><enclosure url="https://cdn.example.test/frieren-02.mp4?token=a" length="42" /></item>
              <item><title>[Group] Frieren - 01 [1080p].mkv</title><guid>frieren-1</guid><category>frieren-season-1</category><enclosure url="https://cdn.example.test/frieren-01.mp4" /></item>
              <item><title>Torrent only</title><guid>torrent</guid><enclosure url="magnet:?xt=urn:btih:abc" /></item>
            </channel></rss>
            """));

        var items = await client.FetchAsync("https://example.test/feed.xml");
        var catalog = RssMediaCatalogReader.BuildCatalog("https://example.test/feed.xml", items);
        var series = Assert.Single(catalog.Series);

        Assert.Equal("frieren-season-1", items[0].Category);
        Assert.Equal((long)(1.5 * 1024 * 1024), items[0].SizeBytes);
        Assert.True(items[0].PublishedAtMs > 0);
        Assert.Equal("Frieren", series.Title);
        Assert.Equal([1d, 2d], series.Episodes.Select(episode => episode.Number));
        Assert.Equal("https://cdn.example.test/frieren-02.mp4?token=a", series.Episodes[1].MediaPath);
        Assert.All(series.Episodes, episode => Assert.StartsWith("rss:", episode.ProgressKey));
    }

    [Fact]
    public async Task RssSourceRegistryTestsLoadsAndRemovesReadOnlyOnlineFeed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"miruplay-rss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        var feedHandler = new TextHandler("""
            <rss version="2.0"><channel><item><title>Example - 01.mkv</title><guid>example-1</guid><category>example</category><enclosure url="https://cdn.example.test/example-01.mp4" /></item></channel></rss>
            """);
        var feedClient = new RssFeedClient(feedHandler);
        using var registry = new MediaSourceRegistry(
            () => settings,
            updated => settings = updated,
            credentials: new MediaSourceCredentialStore(Path.Combine(directory, "credentials")),
            rssMediaCatalogReader: new RssMediaCatalogReader(feedClient));
        var request = new MediaSourceRequest("Online", "RSS", "https://example.test/feed.xml", RecognitionMode: "RSS");

        var tested = await registry.TestAsync(request);
        var added = await registry.AddAsync(request);
        var scan = await registry.ScanAsync(added.Id);
        var episode = Assert.Single(Assert.Single(registry.LoadCatalog(added.Id).Series).Episodes);

        Assert.True(tested.Connected);
        Assert.Equal("RSS", added.Type);
        Assert.Equal("RSS 在线源", registry.Get(added.Id)?.TypeLabel);
        Assert.Equal(1, scan.EpisodesFound);
        Assert.Equal(added.Id, episode.SourceId);
        Assert.StartsWith("https://cdn.example.test/", episode.MediaPath);
        Assert.Equal(2, feedHandler.RequestCount);

        registry.Remove(added.Id);
        Assert.Empty(registry.List());
        Assert.Null(settings.LibraryRoot);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task RssMediaSourceRejectsCredentialsAndCachesSnapshots()
    {
        var handler = new TextHandler("<rss><channel><item><title>A - 01.mkv</title><category>a</category><enclosure url=\"https://cdn.test/a.mp4\" /></item></channel></rss>");
        var reader = new RssMediaCatalogReader(new RssFeedClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => reader.LoadAsync("https://user:secret@example.test/feed.xml"));
        await reader.LoadAsync("https://example.test/feed.xml");
        await reader.LoadAsync("https://example.test/feed.xml");
        Assert.Equal(1, handler.RequestCount);
        reader.Invalidate("https://example.test/feed.xml");
        await reader.LoadAsync("https://example.test/feed.xml");
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ParsesAtomEnclosureAndCategoryAndReportsMalformedFeed()
    {
        var client = new RssFeedClient(new TextHandler("""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
              <title>Example - 01.mkv</title><id>example-1</id><category term="example" />
              <link rel="alternate" href="https://example.test/post/1" />
              <link rel="enclosure" href="https://cdn.test/episode.mp4" />
            </entry></feed>
            """));
        var item = Assert.Single(await client.FetchAsync("https://example.test/feed.xml"));
        Assert.Equal("example", item.Category);
        Assert.Equal("https://example.test/post/1", item.Link);
        Assert.Equal("https://cdn.test/episode.mp4", Assert.Single(Assert.Single(
            RssMediaCatalogReader.BuildCatalog("https://example.test/feed.xml", [item]).Series).Episodes).MediaPath);

        var fallback = Assert.Single(RssMediaCatalogReader.BuildCatalog("https://example.test/feed.xml",
            [new RssFeedItem("Series - Re [1080P].mp4", "special", "https://cdn.test/special.mp4", null, "opaque-category-id")]).Series);
        Assert.Equal("Series - Re [1080P].mp4", fallback.Title);

        var invalidClient = new RssFeedClient(new TextHandler("<rss><channel>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => invalidClient.FetchAsync("https://example.test/feed.xml"));
    }

    [Fact]
    public async Task ParsesRssAndPrefersTorrentEnclosure()
    {
        var client = new RssFeedClient(new TextHandler("""
            <rss version="2.0"><channel><item>
              <title>Frieren 01</title><guid>episode-1</guid>
              <link>https://example.test/post/1</link>
              <enclosure url="https://example.test/Frieren-01.torrent" />
            </item></channel></rss>
            """));

        var item = Assert.Single(await client.FetchAsync("https://example.test/feed.xml"));
        var decision = Assert.Single(RssSubmissionPlanner.Plan([item], "frieren"));

        Assert.Equal("episode-1", decision.ItemKey);
        Assert.Equal("https://example.test/Frieren-01.torrent", decision.SubmissionUrl);
        Assert.Equal(RssSubmissionStatus.WouldSubmit, decision.Status);
    }

    [Fact]
    public async Task ParsesNamespacedAtomAndBuildsStableSha1Key()
    {
        var client = new RssFeedClient(new TextHandler("""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
              <title>Anime 02</title><link href="magnet:?xt=urn:btih:abc" />
            </entry></feed>
            """));

        var item = Assert.Single(await client.FetchAsync("https://example.test/atom.xml"));
        var first = Assert.Single(RssSubmissionPlanner.Plan([item], null));
        var second = Assert.Single(RssSubmissionPlanner.Plan([item], null));

        Assert.Equal("magnet:?xt=urn:btih:abc", first.SubmissionUrl);
        Assert.Equal(40, first.ItemKey?.Length);
        Assert.Equal(first.ItemKey, second.ItemKey);
    }

    [Fact]
    public void StableHashMatchesSha1ProtocolVector()
    {
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", RssSubmissionPlanner.Sha1Hex("abc"));
    }

    [Fact]
    public void PlannerFiltersTitlesAndReportsMissingSubmission()
    {
        var decisions = RssSubmissionPlanner.Plan([
            new RssFeedItem("Keep 01", "1", "magnet:?xt=urn:btih:a", null),
            new RssFeedItem("Skip 02", "2", "magnet:?xt=urn:btih:b", null),
            new RssFeedItem("Keep 03", "3", null, null),
        ], "Keep");

        Assert.Equal([
            RssSubmissionStatus.WouldSubmit,
            RssSubmissionStatus.SkippedFilter,
            RssSubmissionStatus.MissingSubmission,
        ], decisions.Select(decision => decision.Status));
    }

    private sealed class TextHandler(string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            });
        }
    }
}
