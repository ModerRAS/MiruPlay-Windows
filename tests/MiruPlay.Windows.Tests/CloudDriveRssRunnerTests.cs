using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class CloudDriveRssRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-rss-run-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunSubmitsFreshMagnetThenSkipsProcessedItem()
    {
        var environment = CreateEnvironment("""
            <rss version="2.0"><channel>
              <item><title>Keep magnet</title><guid>one</guid><link>magnet:?xt=urn:btih:one</link></item>
              <item><title>Other magnet</title><guid>two</guid><link>magnet:?xt=urn:btih:two</link></item>
              <item><title>Keep missing</title><guid>three</guid></item>
              <item><title>Keep torrent</title><guid>four</guid><link>https://example.test/four.torrent</link></item>
            </channel></rss>
            """, "Keep");
        var submitted = new List<string>();
        var cloud = CreateCloudClient((urls, target) =>
        {
            submitted.AddRange(urls);
            Assert.Equal("/Anime/Downloads", target);
            return Task.CompletedTask;
        });
        var runner = new CloudDriveRssRunner(environment.Config, environment.Credentials, environment.Subscriptions, environment.Feed, environment.Processed, cloud);

        var first = await runner.RunAsync();
        var second = await runner.RunAsync();

        Assert.Equal(new CloudDriveRunSummary(1, 1, 2), first.Summary);
        Assert.Equal(new CloudDriveRunSummary(0, 2, 2), second.Summary);
        Assert.Equal(["magnet:?xt=urn:btih:one"], submitted);
        Assert.True(environment.Processed.IsProcessed(environment.SubscriptionId, "one"));
        Assert.Equal("SUBMITTED", Assert.Single(environment.Processed.ListDownloadTasks()).Status);
        Assert.True(environment.Subscriptions.List()[0].LastCheckedAt > 0);
        Assert.True(environment.Config.Load().LastRunAt > 0);
        Assert.Equal("SUCCEEDED", runner.Status.Status);
    }

    [Fact]
    public async Task FailedCloudSubmissionIsNotMarkedProcessed()
    {
        var environment = CreateEnvironment("""
            <rss version="2.0"><channel><item><title>Keep</title><guid>one</guid><link>magnet:?xt=urn:btih:one</link></item></channel></rss>
            """, null);
        var cloud = CreateCloudClient((_, _) => Task.FromException(new HttpRequestException("offline failed")));
        var runner = new CloudDriveRssRunner(environment.Config, environment.Credentials, environment.Subscriptions, environment.Feed, environment.Processed, cloud);

        var status = await runner.RunAsync();

        Assert.Equal(new CloudDriveRunSummary(0, 0, 1), status.Summary);
        Assert.False(environment.Processed.IsProcessed(environment.SubscriptionId, "one"));
        Assert.Empty(environment.Processed.ListDownloadTasks());
    }

    [Fact]
    public async Task TorrentIsStagedAsFileAndSubmittedAsMagnetBeforeProcessing()
    {
        var environment = CreateEnvironment("""
            <rss version="2.0"><channel><item><title>Show</title><guid>torrent-1</guid><link>https://example.test/show.torrent</link></item></channel></rss>
            """, null);
        var torrent = Encoding.ASCII.GetBytes("d4:infod4:name4:test6:lengthi1eee");
        string? submitted = null;
        var cloud = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/Anime", "", true, true, true, true, false, true)),
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<CloudDriveFileInfo>>([]),
            (_, _, urls, _, _) =>
            {
                submitted = Assert.Single(urls);
                return Task.CompletedTask;
            },
            (_, _, _, _, _) => Task.CompletedTask,
            (_, _, content, _, _, _) =>
            {
                Assert.Equal(torrent, content.ToArray());
                return Task.FromResult("/Anime/Downloads/.miruplay-torrents/show.torrent");
            });
        var preparer = new TorrentSubmissionPreparer(cloud, new TorrentFileDownloader(new TextHandler(torrent)));
        var runner = new CloudDriveRssRunner(
            environment.Config,
            environment.Credentials,
            environment.Subscriptions,
            environment.Feed,
            environment.Processed,
            cloud,
            preparer);

        var status = await runner.RunAsync();

        Assert.Equal(new CloudDriveRunSummary(1, 0, 0), status.Summary);
        Assert.StartsWith("magnet:?xt=urn:btih:", submitted, StringComparison.Ordinal);
        Assert.True(environment.Processed.IsProcessed(environment.SubscriptionId, "torrent-1"));
    }

    [Fact]
    public async Task ExpiredTokenRelogsWithSavedCredentialsAndPersistsReplacement()
    {
        var environment = CreateEnvironment("""
            <rss version="2.0"><channel><item><title>Keep</title><guid>one</guid><link>magnet:?xt=urn:btih:one</link></item></channel></rss>
            """, null);
        environment.Config.Save(environment.Config.Load() with { Username = "cloud-user" });
        environment.Credentials.SavePassword("http://localhost:19798", "saved-password");
        string? submittedToken = null;
        var cloud = new CloudDriveGrpcClient(
            (_, username, password, _) =>
            {
                Assert.Equal("cloud-user", username);
                Assert.Equal("saved-password", password);
                return Task.FromResult(new CloudDriveLoginResult("refreshed-token"));
            },
            (_, token, _) => token == "api-token"
                ? Task.FromException<CloudDriveTokenInfo>(new HttpRequestException("CloudDrive2 API Token 验证失败 (Unauthenticated)。"))
                : Task.FromResult(new CloudDriveTokenInfo("/Anime", "", true, false, false, false, false, true)),
            addOfflineFiles: (_, token, _, _, _) =>
            {
                submittedToken = token;
                return Task.CompletedTask;
            });
        var runner = new CloudDriveRssRunner(
            environment.Config,
            environment.Credentials,
            environment.Subscriptions,
            environment.Feed,
            environment.Processed,
            cloud);

        var status = await runner.RunAsync();

        Assert.Equal(new CloudDriveRunSummary(1, 0, 0), status.Summary);
        Assert.Equal("refreshed-token", submittedToken);
        Assert.Equal("refreshed-token", environment.Credentials.Load().Token);
        Assert.Equal("saved-password", environment.Credentials.Load().Password);
    }

    [Fact]
    public async Task LinkedWebDavRescanContributesIngestionSummary()
    {
        var environment = CreateEnvironment("<rss version=\"2.0\"><channel /></rss>", null);
        environment.Subscriptions.Update(
            environment.SubscriptionId,
            new RssSubscriptionRequest(environment.SubscriptionId, "Anime", "https://example.test/feed.xml", Enabled: false));
        environment.Config.Save(environment.Config.Load() with { WebDavSourceId = 42 });
        long? scanned = null;
        var runner = new CloudDriveRssRunner(
            environment.Config,
            environment.Credentials,
            environment.Subscriptions,
            environment.Feed,
            environment.Processed,
            CreateCloudClient((_, _) => Task.CompletedTask),
            rescanWebDav: (sourceId, _) =>
            {
                scanned = sourceId;
                return Task.FromResult(new CloudDriveIngestionSummary(3, 1, 2));
            });

        var status = await runner.RunAsync();

        Assert.Equal(42, scanned);
        Assert.Equal(new CloudDriveRunSummary(0, 0, 0, 0, 3, 1, 2), status.Summary);
    }

    [Fact]
    public void ProductionWebDavRescanAdapterRejectsErrorResponse()
    {
        var error = Assert.Throws<InvalidDataException>(() => MiruPlay.Windows.MainWindow.ToCloudDriveIngestionSummary(
            new SourceScanResponse(42, "Anime", 0, 0, 0, "remote database failed")));

        Assert.Contains("remote database failed", error.Message);
    }

    [Fact]
    public async Task LinkedWebDavRescanFailureFailsTheRun()
    {
        var environment = CreateEnvironment("<rss version=\"2.0\"><channel /></rss>", null);
        environment.Config.Save(environment.Config.Load() with { WebDavSourceId = 42 });
        var runner = new CloudDriveRssRunner(
            environment.Config,
            environment.Credentials,
            environment.Subscriptions,
            environment.Feed,
            environment.Processed,
            CreateCloudClient((_, _) => Task.CompletedTask),
            rescanWebDav: (_, _) => Task.FromException<CloudDriveIngestionSummary>(new IOException("rescan failed")));

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync());
        Assert.Equal("FAILED", runner.Status.Status);
        Assert.Contains("rescan failed", runner.Status.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private TestEnvironment CreateEnvironment(string xml, string? filter)
    {
        var config = new CloudDriveAutomationStore(Path.Combine(_directory, $"config-{Guid.NewGuid():N}.json"));
        config.Save(new CloudDriveAutomationConfig(
            "http://localhost:19798",
            InboxPath: "/Anime/Downloads",
            LibraryPath: "/Anime/Library",
            LibraryMode: CloudDriveLibraryMode.SingleDirectory,
            Enabled: true));
        var credentials = new CloudDriveCredentialStore(Path.Combine(_directory, $"credentials-{Guid.NewGuid():N}.bin"));
        credentials.SaveToken("http://localhost:19798", "api-token");
        var subscriptions = new RssSubscriptionStore(Path.Combine(_directory, $"subscriptions-{Guid.NewGuid():N}.json"));
        var subscription = subscriptions.Add(new RssSubscriptionRequest(0, "Anime", "https://example.test/feed.xml", filter));
        var feed = new RssFeedClient(new TextHandler(xml));
        var processed = new RssProcessedStore(Path.Combine(_directory, $"state-{Guid.NewGuid():N}.db"));
        return new TestEnvironment(config, credentials, subscriptions, feed, processed, subscription.Id);
    }

    private static CloudDriveGrpcClient CreateCloudClient(Func<IReadOnlyList<string>, string, Task> submit) => new(
        (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
        (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/Anime", "MiruPlay", true, false, false, false, false, true)),
        addOfflineFiles: (_, token, urls, target, _) =>
        {
            Assert.Equal("api-token", token);
            return submit(urls, target);
        });

    private sealed record TestEnvironment(
        CloudDriveAutomationStore Config,
        CloudDriveCredentialStore Credentials,
        RssSubscriptionStore Subscriptions,
        RssFeedClient Feed,
        RssProcessedStore Processed,
        long SubscriptionId);

    private sealed class TextHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _mediaType;

        public TextHandler(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            _mediaType = "application/xml";
        }

        public TextHandler(byte[] body)
        {
            _body = body;
            _mediaType = "application/octet-stream";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(_body) { Headers = { ContentType = new(_mediaType) } },
            });
    }
}
