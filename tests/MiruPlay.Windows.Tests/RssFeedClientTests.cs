using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RssFeedClientTests
{
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            });
    }
}
