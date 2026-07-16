using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class TmdbMetadataClientTests
{
    [Fact]
    public async Task SearchUsesBearerTokenChineseLocaleYearAndBoundedResults()
    {
        var handler = new RecordingHandler("""
            { "results": [
              { "id": 209867, "name": "葬送的芙莉莲", "original_name": "Frieren", "overview": "Journey", "first_air_date": "2023-09-29", "poster_path": "/poster.jpg", "backdrop_path": "/backdrop.jpg" }
            ] }
            """);
        using var client = new TmdbMetadataClient(handler, new Uri("https://tmdb.test/3/"));

        var results = await client.SearchAsync("Frieren", "read-token", 2023, 99);

        var result = Assert.Single(results);
        Assert.Equal(209867, result.Id);
        Assert.Equal("TMDB", result.Provider);
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", result.PosterUrl);
        Assert.Equal("Bearer read-token", handler.Authorization);
        Assert.Contains("query=Frieren", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("language=zh-CN", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("first_air_date_year=2023", handler.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchRequiresTokenBeforeNetwork()
    {
        var handler = new RecordingHandler("{\"results\":[]}");
        using var client = new TmdbMetadataClient(handler, new Uri("https://tmdb.test/3/"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("Frieren", ""));

        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string Authorization { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
