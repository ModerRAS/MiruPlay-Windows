using System.Net;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MetadataSearchServiceTests
{
    [Fact]
    public void PlanDeduplicatesAliasesAndExtractsSeasonAndYearHints()
    {
        var plan = MetadataSearchService.Plan(new MetadataSearchRequest(
            "Example Show Season 2 2024", "drama", Title: "Example Show Season 2 2024",
            OriginalTitle: "Example Show", Aliases: ["Example Show", "Alias"],
            FilePathSamples: [@"D:\Media\Alias\Example.Show.S02E03.1080p.mkv"]));

        Assert.Equal(2, plan.SeasonHint);
        Assert.Equal(2024, plan.YearHint);
        Assert.Equal(plan.Queries.Count, plan.Queries.Select(item => item.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(plan.Queries, item => item.Text == "Example Show");
        Assert.Contains(plan.Queries, item => item.Text == "Alias");
        Assert.Equal(12, MetadataSearchService.Plan(new MetadataSearchRequest("Example Show 第十二季", "drama")).SeasonHint);
    }

    [Fact]
    public async Task SearchClustersProvidersReranksAndCachesSuccessfulResults()
    {
        var bangumiHandler = new JsonHandler("""{"data":[{"id":42,"type":2,"name":"Cowboy Bebop","name_cn":"星际牛仔","summary":"Space western","date":"1998-04-03"}]}""");
        var tmdbHandler = new JsonHandler("""{"results":[{"id":7,"name":"Cowboy Bebop","original_name":"Cowboy Bebop","overview":"Space western","first_air_date":"1998-04-03"}]}""");
        var tvMazeHandler = new JsonHandler("""[{"score":1,"show":{"id":9,"name":"Cowboy Bebop","name_original":"Cowboy Bebop","summary":"<p>Space western</p>","premiered":"1998-04-03"}}]""");
        using var bangumi = new BangumiMetadataClient(bangumiHandler, new("https://bgm.test/v0/"));
        using var tmdb = new TmdbMetadataClient(tmdbHandler, new("https://tmdb.test/3/"));
        using var tvMaze = new TvMazeMetadataClient(tvMazeHandler, new("https://tvmaze.test/"));
        var service = new MetadataSearchService(bangumi, tmdb, tvMaze);
        var request = new MetadataSearchRequest("Cowboy Bebop", "drama", YearHint: 1998);

        var first = await service.SearchAsync(request, "test-token");
        var candidate = Assert.Single(first.Candidates);
        Assert.Equal(2, candidate.Providers.Count);
        Assert.Equal("TMDB", candidate.Providers[0].Provider);
        Assert.Equal("TVMaze", candidate.Providers[1].Provider);
        Assert.Equal("AUTO_ACCEPT", candidate.Recommendation);
        Assert.Contains("多个来源收敛", candidate.Evidence);

        var cached = await service.SearchAsync(request, "test-token");
        Assert.True(cached.FromCache);
        Assert.Equal(0, bangumiHandler.RequestCount);
        Assert.Equal(1, tmdbHandler.RequestCount);
        Assert.Equal(1, tvMazeHandler.RequestCount);
    }

    [Fact]
    public async Task ProviderFailureDoesNotDiscardSuccessfulSources()
    {
        var bangumiHandler = new JsonHandler("{}");
        var tmdbHandler = new JsonHandler("{}", HttpStatusCode.ServiceUnavailable);
        var tvMazeHandler = new JsonHandler("""[{"show":{"id":8,"name":"Example Show"}}]""");
        using var bangumi = new BangumiMetadataClient(bangumiHandler, new("https://bgm.test/v0/"));
        using var tmdb = new TmdbMetadataClient(tmdbHandler, new("https://tmdb.test/3/"));
        using var tvMaze = new TvMazeMetadataClient(tvMazeHandler, new("https://tvmaze.test/"));
        var response = await new MetadataSearchService(bangumi, tmdb, tvMaze)
            .SearchAsync(new MetadataSearchRequest("Example Show", "drama"), "test-token");

        Assert.Single(response.Candidates);
        Assert.Equal("TVMaze", Assert.Single(Assert.Single(response.Candidates).Providers).Provider);
        Assert.Contains(response.ProviderErrors, value => value.StartsWith("TMDB:", StringComparison.Ordinal));
        Assert.False(response.FromCache);
    }

    [Fact]
    public async Task AnimeSearchUsesOnlyBangumi()
    {
        var bangumiHandler = new JsonHandler("""{"data":[{"id":42,"type":2,"name":"Example Show"}]}""");
        var tmdbHandler = new JsonHandler("""{"results":[]}""");
        var tvMazeHandler = new JsonHandler("[]");
        using var bangumi = new BangumiMetadataClient(bangumiHandler, new("https://bgm.test/v0/"));
        using var tmdb = new TmdbMetadataClient(tmdbHandler, new("https://tmdb.test/3/"));
        using var tvMaze = new TvMazeMetadataClient(tvMazeHandler, new("https://tvmaze.test/"));

        var result = await new MetadataSearchService(bangumi, tmdb, tvMaze)
            .SearchAsync(new MetadataSearchRequest("Example Show", "anime"), "test-token");

        Assert.Equal("Bangumi", Assert.Single(Assert.Single(result.Candidates).Providers).Provider);
        Assert.Equal(1, bangumiHandler.RequestCount);
        Assert.Equal(0, tmdbHandler.RequestCount);
        Assert.Equal(0, tvMazeHandler.RequestCount);
    }

    [Fact]
    public async Task DramaSearchKeepsSeasonConflictsAndSameProviderIdsSeparate()
    {
        var bangumiHandler = new JsonHandler("{}");
        var tmdbHandler = new JsonHandler("""{"results":[{"id":7,"name":"Example Show Season 1"},{"id":8,"name":"Example Show Season 2"}]}""");
        var tvMazeHandler = new JsonHandler("""[{"show":{"id":9,"name":"Example Show Season 2"}}]""");
        using var bangumi = new BangumiMetadataClient(bangumiHandler, new("https://bgm.test/v0/"));
        using var tmdb = new TmdbMetadataClient(tmdbHandler, new("https://tmdb.test/3/"));
        using var tvMaze = new TvMazeMetadataClient(tvMazeHandler, new("https://tvmaze.test/"));

        var result = await new MetadataSearchService(bangumi, tmdb, tvMaze)
            .SearchAsync(new MetadataSearchRequest("Example Show Season 1", "drama"), "test-token");

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, item => item.Providers.Count == 1 && item.Providers[0].Id == "7");
        Assert.Contains(result.Candidates, item => item.Providers.Count == 2 && item.Providers.Any(provider => provider.Id == "8") &&
            item.Providers.Any(provider => provider.Id == "9"));
        Assert.Equal(0, bangumiHandler.RequestCount);
    }

    private sealed class JsonHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
