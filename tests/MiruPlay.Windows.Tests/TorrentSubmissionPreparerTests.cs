using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class TorrentSubmissionPreparerTests
{
    private static readonly byte[] TorrentBytes = Encoding.ASCII.GetBytes(
        "d8:announce14:https://t.test4:infod4:name4:test6:lengthi1eee");

    [Fact]
    public void ParserBuildsAndroidCompatibleMagnetFromExactInfoDictionary()
    {
        var magnet = TorrentMagnetParser.Parse(TorrentBytes);

        Assert.Equal(
            "magnet:?xt=urn:btih:13fdbc500353cc14e9c170e2f755993eeaa9fb8d&dn=test&tr=https%3A%2F%2Ft.test",
            magnet);
        Assert.Throws<InvalidDataException>(() => TorrentMagnetParser.Parse("d4:infodejunk"u8));
    }

    [Fact]
    public async Task DownloaderBoundsContentAndBuildsSafeName()
    {
        var downloader = new TorrentFileDownloader(new ContentHandler(TorrentBytes));

        var result = await downloader.DownloadAsync(
            "https://example.test/show.torrent",
            "Bad:/Name",
            "abc123",
            false,
            "",
            0);

        Assert.Equal(TorrentBytes, result.Content.ToArray());
        Assert.Equal("abc123-show.torrent", result.FileName);
        var oversized = new TorrentFileDownloader(new ContentHandler(new byte[16 * 1024 * 1024 + 1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => oversized.DownloadAsync(
            "https://example.test/show.torrent", "Show", "key", false, "", 0));
    }

    [Fact]
    public async Task PreparerCreatesStagingUploadsTorrentAndReturnsMagnet()
    {
        string? createdParent = null;
        string? uploadedParent = null;
        string? uploadedName = null;
        byte[]? uploaded = null;
        var cloud = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/Anime", "", true, true, true, true, false, true)),
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<CloudDriveFileInfo>>([]),
            createFolder: (_, token, parent, name, _) =>
            {
                Assert.Equal("api-token", token);
                createdParent = $"{parent}/{name}";
                return Task.CompletedTask;
            },
            uploadFile: (_, token, content, parent, name, _) =>
            {
                Assert.Equal("api-token", token);
                uploadedParent = parent;
                uploadedName = name;
                uploaded = content.ToArray();
                return Task.FromResult($"{parent}/{name}");
            });
        var preparer = new TorrentSubmissionPreparer(cloud, new TorrentFileDownloader(new ContentHandler(TorrentBytes)));
        var config = new CloudDriveAutomationConfig(
            "http://localhost:19798",
            InboxPath: "/Anime/Downloads",
            LibraryPath: "/Anime/Library",
            Enabled: true);
        var decision = new RssSubmissionDecision(
            new RssFeedItem("Show", "item-1", "https://example.test/show.torrent", null),
            "https://example.test/show.torrent",
            "item-1",
            RssSubmissionStatus.WouldSubmit);

        var magnet = await preparer.PrepareAsync(
            config,
            new CloudDriveTokenInfo("/Anime", "", true, true, true, true, false, true),
            "api-token",
            decision);

        Assert.StartsWith("magnet:?xt=urn:btih:13fdbc500353cc14e9c170e2f755993eeaa9fb8d", magnet, StringComparison.Ordinal);
        Assert.Equal("/Anime/Downloads/.miruplay-torrents", createdParent);
        Assert.Equal("/Anime/Downloads/.miruplay-torrents", uploadedParent);
        Assert.EndsWith("-show.torrent", uploadedName, StringComparison.Ordinal);
        Assert.Equal(TorrentBytes, uploaded);
    }

    private sealed class ContentHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(content),
            });
    }
}
