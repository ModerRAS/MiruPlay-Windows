using System.Net;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class TvMazeMetadataClientTests
{
    [Fact]
    public async Task SearchNormalizesEpisodesIntoMetadataCandidatesAndStripsHtml()
    {
        using var client = new TvMazeMetadataClient(new Handler("""[{"score":42,"show":{"id":123,"name":"Example","summary":"<p>A &amp; B</p><script>hidden</script>","premiered":"2026-09-01","image":{"medium":"https://images.example.test/medium.jpg","original":"http://unsafe.test/poster.jpg"}}}]"""), new("https://tvmaze.test/"));
        var result = Assert.Single(await client.SearchAsync("Example"));
        Assert.Equal(123, result.Id);
        Assert.Equal("Example", result.Title);
        Assert.Equal("A & B hidden", result.Summary);
        Assert.Equal("2026-09-01", result.FirstAirDate);
        Assert.Equal("https://images.example.test/medium.jpg", result.PosterUrl);
        Assert.Null(result.BackdropUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task SearchBoundsResults(int limit)
    {
        using var client = new TvMazeMetadataClient(new Handler("""[{"show":{"id":1,"name":"One"}},{"show":{"id":2,"name":"Two"}}]"""), new("https://tvmaze.test/"));
        Assert.Equal(Math.Clamp(limit, 1, 20) == 1 ? 1 : 2, (await client.SearchAsync("x", limit)).Count);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
