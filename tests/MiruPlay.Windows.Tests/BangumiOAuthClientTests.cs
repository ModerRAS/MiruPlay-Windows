using System.Net;
using System.Net.Http;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiOAuthClientTests
{
    private const string TokenJson = """
        {"access_token":"access-1","expires_in":604800,"token_type":"Bearer","refresh_token":"refresh-1","user_id":42}
        """;

    [Fact]
    public void AuthorizeUrlContainsProtocolFieldsAndEncodesValues()
    {
        var url = BangumiOAuthConstants.BuildAuthorizeUrl(
            "client id",
            "state&x",
            "http://127.0.0.1:13712/callback");

        Assert.StartsWith("https://bgm.tv/oauth/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("client_id=client%20id", url, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url, StringComparison.Ordinal);
        Assert.Contains("state=state%26x", url, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A13712%2Fcallback", url, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeCodePostsFormAndParsesTokens()
    {
        var handler = new RecordingHandler(_ => JsonResponse(TokenJson));
        using var client = new BangumiOAuthClient(
            "client-id",
            "client-secret",
            handler,
            new Uri("https://bgm.test/oauth/access_token"),
            clockMillis: () => 1_000_000);

        var tokens = await client.ExchangeCodeAsync("code-1", "http://127.0.0.1:13712/callback");

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal(1_000_000 + 604_800_000, tokens.ExpiresAtMillis);
        Assert.Equal(42, tokens.UserId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://bgm.test/oauth/access_token", request.RequestUri!.AbsoluteUri);
        Assert.Equal(BangumiOAuthConstants.UserAgent, request.Headers.UserAgent.ToString());
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains("code=code-1", body, StringComparison.Ordinal);
        Assert.Contains("client_secret=client-secret", body, StringComparison.Ordinal);
        // The secret only travels in the token POST body, never the URL.
        Assert.DoesNotContain("client-secret", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshPostsRefreshGrantAndRotatesRefreshToken()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {"access_token":"access-2","expires_in":604800,"refresh_token":"refresh-2","user_id":42}
            """));
        using var client = new BangumiOAuthClient(
            "client-id",
            "client-secret",
            handler,
            new Uri("https://bgm.test/oauth/access_token"),
            clockMillis: () => 2_000_000);

        var tokens = await client.RefreshAsync("refresh-1", "http://127.0.0.1:13712/callback");

        Assert.Equal("access-2", tokens.AccessToken);
        Assert.Equal("refresh-2", tokens.RefreshToken);
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredClientFailsBeforeAnyNetworkRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("network must not be used"));
        using var client = new BangumiOAuthClient(string.Empty, string.Empty, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ExchangeCodeAsync("code", "http://127.0.0.1:13712/callback"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingAccessTokenIsRejected()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"expires_in":604800}"""));
        using var client = new BangumiOAuthClient("id", "secret", handler, new Uri("https://bgm.test/oauth/access_token"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ExchangeCodeAsync("code", "http://127.0.0.1:13712/callback"));
    }

    [Fact]
    public async Task HttpFailureSurfacesStatusButNotResponseBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("very-secret-server-error"),
        });
        using var client = new BangumiOAuthClient("id", "secret", handler, new Uri("https://bgm.test/oauth/access_token"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ExchangeCodeAsync("code", "http://127.0.0.1:13712/callback"));
        Assert.DoesNotContain("very-secret-server-error", error.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task CurrentUserUsesBearerTokenAndParsesProfile()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"id":7,"username":"miru","nickname":"Miru"}"""));
        using var client = new BangumiOAuthClient(
            "id",
            "secret",
            handler,
            new Uri("https://bgm.test/oauth/access_token"),
            new Uri("https://bgm.test/v0/me"));

        var user = await client.GetCurrentUserAsync("access-1");

        Assert.Equal(7, user.Id);
        Assert.Equal("miru", user.Username);
        Assert.Equal("Miru", user.Nickname);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer access-1", request.Headers.Authorization?.ToString());
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}

public sealed class BangumiOAuthCredentialStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "miruplay-oauth-credentials-" + Guid.NewGuid().ToString("N") + ".bin");

    [Fact]
    public void CredentialsAreDpapiProtectedAndRoundTrip()
    {
        var store = new BangumiOAuthCredentialStore(_path);

        store.Save("client-id-value", "client-secret-value");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("client-id-value", loaded.ClientId);
        Assert.Equal("client-secret-value", loaded.ClientSecret);
        Assert.Equal("settings", loaded.Source);
        var encrypted = File.ReadAllBytes(_path);
        var text = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("client-secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("client-id-value", text, StringComparison.Ordinal);

        store.Clear();
        Assert.Null(store.Load());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void InvalidCredentialLengthsAreRejected()
    {
        var store = new BangumiOAuthCredentialStore(_path);
        Assert.Throws<ArgumentException>(() => store.Save(" ", "secret"));
        Assert.Throws<ArgumentException>(() => store.Save("id", " "));
        Assert.Throws<ArgumentException>(() => store.Save(new string('a', 257), "secret"));
    }

    [Fact]
    public void EnvironmentVariablesWinOverStoredCredentials()
    {
        var store = new BangumiOAuthCredentialStore(_path);
        store.Save("stored-id", "stored-secret");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["BANGUMI_CLIENT_ID"] = "env-id",
            ["BANGUMI_CLIENT_SECRET"] = "env-secret",
        };

        var credentials = BangumiOAuthCredentialsProvider.Resolve(store, name => environment.GetValueOrDefault(name));

        Assert.NotNull(credentials);
        Assert.Equal("env-id", credentials.ClientId);
        Assert.Equal("env-secret", credentials.ClientSecret);
        Assert.Equal("environment", credentials.Source);
    }

    [Fact]
    public void FallsBackToStoredCredentialsAndReportsUnconfigured()
    {
        var store = new BangumiOAuthCredentialStore(_path);
        Assert.Null(BangumiOAuthCredentialsProvider.Resolve(store, _ => null));

        store.Save("stored-id", "stored-secret");
        var credentials = BangumiOAuthCredentialsProvider.Resolve(store, _ => string.Empty);
        Assert.NotNull(credentials);
        Assert.Equal("settings", credentials.Source);
        Assert.Equal("stored-id", credentials.ClientId);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public sealed class OutboundHttpProxyTests
{
    [Fact]
    public void ConfiguredProxyRoutesPublicHostsAndBypassesLoopback()
    {
        using var handler = OutboundHttpProxy.CreateHandler(() => new CloudDriveAutomationConfig(
            RssProxyEnabled: true,
            RssProxyHost: "127.0.0.1",
            RssProxyPort: 7890));
        var proxy = handler.Proxy;
        Assert.NotNull(proxy);

        var tokenUri = new Uri("https://bgm.tv/oauth/access_token");
        Assert.False(proxy.IsBypassed(tokenUri));
        Assert.Equal(new Uri("http://127.0.0.1:7890/"), proxy.GetProxy(tokenUri));

        var loopback = new Uri("http://127.0.0.1:13712/callback");
        Assert.True(proxy.IsBypassed(loopback));
        Assert.Equal(loopback, proxy.GetProxy(loopback));
    }

    [Fact]
    public void DisabledProxyBypassesEverything()
    {
        using var handler = OutboundHttpProxy.CreateHandler(() => new CloudDriveAutomationConfig());
        var proxy = handler.Proxy;
        Assert.NotNull(proxy);
        Assert.True(proxy.IsBypassed(new Uri("https://bgm.tv/oauth/access_token")));
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1:13712/callback")));
    }
}
