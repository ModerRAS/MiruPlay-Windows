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
        Assert.Equal(["Anime/01.en.m4a"], file.ExternalAudioPaths);
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

    [Fact]
    public async Task ReadsListedNfoWithSameScopedCredentialsAndBounds()
    {
        var handler = new DirectoryHandler("Basic YWxpY2U6c2VjcmV0", nfo: "valid");
        using var enumerator = new WebDavDirectoryEnumerator(handler, TimeSpan.Zero);
        var file = Assert.Single(await enumerator.EnumerateAsync("https://example.test/dav", new("alice", "secret")));
        Assert.Equal("NFO show", file.SeriesMetadata?.Title);
        Assert.Equal("42", Assert.Single(file.SeriesMetadata!.UniqueIds!).Value);
        Assert.Equal("NFO episode", file.EpisodeMetadata?.Title);
        Assert.Equal(2, file.EpisodeMetadata?.Season);
        Assert.Equal(3, file.EpisodeMetadata?.Episode);
        Assert.Equal(2, handler.NfoRequests);
        Assert.All(handler.Authorizations, value => Assert.Equal("Basic YWxpY2U6c2VjcmV0", value));
    }

    [Fact]
    public async Task ListsPlaybackSiblingsWithoutLeavingTheSourceRoot()
    {
        using var enumerator = new WebDavDirectoryEnumerator(new DirectoryHandler(null), TimeSpan.Zero);

        var siblings = await enumerator.ListSiblingFilesAsync(
            "https://example.test/dav",
            new Uri("https://example.test/dav/Anime/01.mkv"),
            null);

        Assert.Contains(siblings, uri => uri.AbsoluteUri == "https://example.test/dav/Anime/01.en.m4a");
        Assert.DoesNotContain(siblings, uri => uri.AbsoluteUri.EndsWith("/Anime/", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => enumerator.ListSiblingFilesAsync(
            "https://example.test/dav",
            new Uri("https://example.test/escape.mkv"),
            null));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("oversized")]
    public async Task RejectsUnsafeOrOversizedRemoteNfo(string kind)
    {
        using var enumerator = new WebDavDirectoryEnumerator(new DirectoryHandler(null, nfo: kind), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidDataException>(() => enumerator.EnumerateAsync("https://example.test/dav", null));
    }

    private sealed class RecordingProgress : IProgress<DirectoryScanProgress>
    {
        public List<DirectoryScanProgress> Values { get; } = [];
        public void Report(DirectoryScanProgress value) => Values.Add(value);
    }

    private sealed class DirectoryHandler(string? expectedAuthorization, bool includeOutsideResponse = false, string? nfo = null) : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];
        public int NfoRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (expectedAuthorization is not null)
                Assert.Equal(expectedAuthorization, request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                NfoRequests++;
                Assert.EndsWith(".nfo", path, StringComparison.Ordinal);
                var text = nfo switch
                {
                    "invalid" => "<!DOCTYPE x [<!ENTITY ex SYSTEM 'file:///secret'>]><episodedetails><title>&ex;</title></episodedetails>",
                    "oversized" => new string('x', 4 * 1024 * 1024 + 1),
                    _ when path.EndsWith("tvshow.nfo", StringComparison.Ordinal) => "<tvshow><title>NFO show</title><uniqueid type='tmdb'>42</uniqueid></tvshow>",
                    _ => "<episodedetails><title>NFO episode</title><season>2</season><episode>3</episode></episodedetails>",
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
            }
            var body = path.EndsWith("/Anime/", StringComparison.Ordinal)
                ? AnimeListing(nfo is not null)
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

        private static string AnimeListing(bool includeNfo) => $"""
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
              <d:response>
                <d:href>https://example.test/dav/Anime/01.en.m4a</d:href>
                <d:propstat><d:prop><d:getcontentlength>8</d:getcontentlength></d:prop></d:propstat>
              </d:response>
              {(includeNfo ? "<d:response><d:href>https://example.test/dav/Anime/01.nfo</d:href></d:response><d:response><d:href>https://example.test/dav/Anime/tvshow.nfo</d:href></d:response>" : "")}
            </d:multistatus>
            """;
    }
}
