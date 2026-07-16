using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiMetadataClientTests
{
    [Fact]
    public async Task SearchUsesAnimeFilterUserAgentAndBoundedLimit()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {
              "data": [
                {
                  "id": 431767,
                  "type": 2,
                  "name": "Sousou no Frieren",
                  "name_cn": "葬送的芙莉莲",
                  "summary": "Journey",
                  "date": "2023-09-29",
                  "images": { "large": "https://lain.bgm.tv/pic/cover/l/test.jpg" }
                },
                { "id": 1, "type": 6, "name": "Not anime" }
              ]
            }
            """);
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        var results = await client.SearchAsync("  Frieren  ", 99);

        var result = Assert.Single(results);
        Assert.Equal(431767, result.Id);
        Assert.Equal("葬送的芙莉莲", result.TitleCn);
        Assert.Equal("https://bgm.tv/subject/431767", result.DetailUrl);
        Assert.Equal("https://bangumi.test/v0/search/subjects?limit=20", handler.RequestUri?.AbsoluteUri);
        Assert.Contains("ModerRAS/MiruPlay-Windows/0.1", handler.UserAgent, StringComparison.Ordinal);
        Assert.Contains("\"keyword\":\"Frieren\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":[2]", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchRejectsEmptyAndOversizedQueriesBeforeNetwork()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(new string('x', 101)));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CurrentUserUsesBearerToken()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"id\":42,\"username\":\"alice\",\"nickname\":\"Alice\"}");
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        var user = await client.GetCurrentUserAsync("access-token");

        Assert.Equal(42, user.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal("Bearer access-token", handler.Authorization);
        Assert.Equal("https://bangumi.test/v0/me", handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task SubjectCollectionUsesCurrentUserEndpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"subject_id\":431767,\"type\":3,\"rate\":9,\"ep_status\":12,\"updated_at\":\"2026-07-14T00:00:00Z\"}");
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        var collection = await client.GetSubjectCollectionAsync(431767, "access-token");

        Assert.NotNull(collection);
        Assert.Equal(3, collection.Type);
        Assert.Equal(12, collection.EpisodeStatus);
        Assert.Equal("https://bangumi.test/v0/users/-/collections/431767", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer access-token", handler.Authorization);
    }

    [Fact]
    public async Task UpsertSubjectCollectionPostsValidatedType()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent, string.Empty);
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        await client.UpsertSubjectCollectionAsync(431767, 3, "access-token");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("\"type\":3", handler.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer access-token", handler.Authorization);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.UpsertSubjectCollectionAsync(431767, 6, "access-token"));
    }

    [Fact]
    public async Task UpdateEpisodeCollectionPutsValidatedType()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent, string.Empty);
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        await client.UpdateEpisodeCollectionAsync(9876, 2, "access-token");

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("https://bangumi.test/v0/users/-/collections/-/episodes/9876", handler.RequestUri?.AbsoluteUri);
        Assert.Contains("\"type\":2", handler.Body, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UpdateEpisodeCollectionAsync(9876, 4, "access-token"));
    }

    [Fact]
    public async Task EpisodeCollectionsMapSnakeCaseFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"data\":[{\"episode_id\":9876,\"episode_number\":12,\"type\":2,\"updated_at\":12345}],\"total\":1}");
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        var values = await client.GetEpisodeCollectionsAsync(431767, "access-token");

        var value = Assert.Single(values);
        Assert.Equal(9876, value.EpisodeId);
        Assert.Equal(12, value.EpisodeNumber);
        Assert.Equal(2, value.Type);
        Assert.Contains("episode_type=0", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer access-token", handler.Authorization);
    }

    [Fact]
    public async Task EpisodeCollectionsReadAllPages()
    {
        var handler = new SequenceHandler(
            "{\"data\":[{\"episode_id\":1,\"episode_number\":1,\"type\":2,\"updated_at\":1},{\"episode_id\":2,\"episode_number\":2,\"type\":2,\"updated_at\":2}],\"total\":3}",
            "{\"data\":[{\"episode_id\":3,\"episode_number\":3,\"type\":1,\"updated_at\":3}],\"total\":3}");
        using var client = new BangumiMetadataClient(handler, new Uri("https://bangumi.test/v0/"));

        var values = await client.GetEpisodeCollectionsAsync(431767, "access-token");

        Assert.Equal([1, 2, 3], values.Select(value => value.EpisodeId));
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("offset=0", handler.RequestUris[0].Query, StringComparison.Ordinal);
        Assert.Contains("offset=2", handler.RequestUris[1].Query, StringComparison.Ordinal);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer access-token", value));
    }

    [Fact]
    public async Task SearchReportsUpstreamHttpStatus()
    {
        using var client = new BangumiMetadataClient(
            new RecordingHandler(HttpStatusCode.TooManyRequests, "{}"),
            new Uri("https://bangumi.test/v0/"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("Frieren"));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    private sealed class SequenceHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responseBodies);
        public List<Uri> RequestUris { get; } = [];
        public List<string> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string UserAgent { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Method = request.Method;
            UserAgent = request.Headers.UserAgent.ToString();
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
