using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class DirectoryMediaSourceRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-directory-source-{Guid.NewGuid():N}");

    [Fact]
    public async Task WebDavDirectorySourceValidatesScansAndLoadsCatalog()
    {
        var handler = new StaticWebDavDirectoryHandler();
        using var directoryEnumerator = new WebDavDirectoryEnumerator(handler, TimeSpan.Zero);
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        using var registry = new MediaSourceRegistry(
            () => settings,
            updated => settings = updated,
            webDavDirectory: directoryEnumerator,
            directoryIndex: new DirectoryLibraryIndex(Path.Combine(_root, "index.db")));
        var request = new MediaSourceRequest(
            "Remote Directory",
            "WEBDAV",
            "https://example.test/dav",
            Username: "alice",
            Password: "secret",
            RecognitionMode: "DIRECTORY");

        var test = await registry.TestAsync(request);
        var added = await registry.AddAsync(request);
        var scan = await registry.ScanAsync(added.Id);
        var episode = Assert.Single(Assert.Single(registry.LoadCatalog(added.Id).Series).Episodes);

        Assert.True(test.Connected);
        Assert.Equal("WEBDAV", added.Type);
        Assert.Equal("DIRECTORY", registry.Get(added.Id)?.RecognitionMode);
        Assert.Equal(1, scan.EpisodesFound);
        Assert.Equal("https://example.test/dav/Anime/01.mkv", episode.MediaPath);
        Assert.Equal("https://example.test/dav/Anime/01.ass", Assert.Single(episode.SubtitlePaths));
        Assert.Equal(new MediaSourceCredential("alice", "secret"), registry.GetCredential(added.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class StaticWebDavDirectoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/Anime/", StringComparison.Ordinal)
                ? AnimeListing()
                : RootListing();
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            });
        }

        private static string RootListing() => """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>https://example.test/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>https://example.test/dav/Anime/</d:href><d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat></d:response>
            </d:multistatus>
            """;

        private static string AnimeListing() => """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>https://example.test/dav/Anime/</d:href><d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>https://example.test/dav/Anime/01.mkv</d:href><d:propstat><d:prop><d:getcontentlength>12</d:getcontentlength></d:prop></d:propstat></d:response>
              <d:response><d:href>https://example.test/dav/Anime/01.ass</d:href><d:propstat><d:prop><d:getcontentlength>4</d:getcontentlength></d:prop></d:propstat></d:response>
            </d:multistatus>
            """;
    }
}
