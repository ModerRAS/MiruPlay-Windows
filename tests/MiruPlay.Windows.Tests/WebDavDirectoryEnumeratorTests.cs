using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class WebDavDirectoryEnumeratorTests
{
    [Fact]
    public async Task EnumeratesNestedMediaAndSubtitlesWithScopedCredentials()
    {
        const string root = "https://example.test/dav";
        var expectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));
        var handler = new DirectoryHandler(expectedAuthorization);
        using var enumerator = new WebDavDirectoryEnumerator(handler, TimeSpan.Zero);
        var progress = new RecordingProgress();

        await enumerator.ValidateAsync(root, new MediaSourceCredential("alice", "secret"));
        var files = await enumerator.EnumerateAsync(
            root,
            new MediaSourceCredential("alice", "secret"),
            progress);

        var file = Assert.Single(files);
        Assert.Equal("Anime/01.mkv", file.RelativePath);
        Assert.Equal(["Anime/01.zh-Hans.ass"], file.SubtitlePaths);
        Assert.Equal(12, file.SizeBytes);
        Assert.NotEmpty(progress.Values);
        Assert.All(handler.Authorizations, value => Assert.Equal(expectedAuthorization, value));
    }

    [Fact]
    public async Task RejectsResponseOutsideSourceRoot()
    {
        using var enumerator = new WebDavDirectoryEnumerator(new DirectoryHandler(null, includeOutsideResponse: true), TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidDataException>(() => enumerator.EnumerateAsync(
            "https://example.test/dav",
            null));
    }

    private sealed class RecordingProgress : IProgress<DirectoryScanProgress>
    {
        public List<DirectoryScanProgress> Values { get; } = [];
        public void Report(DirectoryScanProgress value) => Values.Add(value);
    }

    private sealed class DirectoryHandler(string? expectedAuthorization, bool includeOutsideResponse = false) : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (expectedAuthorization is not null)
                Assert.Equal(expectedAuthorization, request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/Anime/", StringComparison.Ordinal)
                ? AnimeListing()
                : RootListing(includeOutsideResponse);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            });
        }

        private static string RootListing(bool includeOutsideResponse) => $"""
            <?xml version="1.0" encoding="utf-8" ?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>https://example.test/dav/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>https://example.test/dav/Anime/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat>
              </d:response>
              {(includeOutsideResponse ? "<d:response><d:href>https://example.test/escape.mkv</d:href></d:response>" : "")}
            </d:multistatus>
            """;

        private static string AnimeListing() => """
            <?xml version="1.0" encoding="utf-8" ?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>https://example.test/dav/Anime/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>https://example.test/dav/Anime/01.mkv</d:href>
                <d:propstat><d:prop><d:getcontentlength>12</d:getcontentlength><d:getlastmodified>Wed, 01 Jan 2025 00:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>https://example.test/dav/Anime/01.zh-Hans.ass</d:href>
                <d:propstat><d:prop><d:getcontentlength>4</d:getcontentlength></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
    }
}
