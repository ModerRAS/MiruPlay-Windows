using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiLoginApiTests : IDisposable
{
    private const string OAuthTokenJson = """
        {"access_token":"new-access","expires_in":604800,"refresh_token":"new-refresh","user_id":42}
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miruplay-bangumi-api-" + Guid.NewGuid().ToString("N"));

    public BangumiLoginApiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task OAuthRoutesRequireTheExistingWebControlToken()
    {
        await using var harness = await StartHarnessAsync();
        using var unauthorized = new HttpClient { BaseAddress = new Uri($"http://localhost:{harness.Port}") };

        var denied = await unauthorized.GetAsync("/api/metadata/bangumi/oauth");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var envelope = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());

        var allowed = await harness.Client.GetAsync("/api/metadata/bangumi/oauth");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task OAuthStatusAndCredentialRoutesNeverEchoTheSecret()
    {
        await using var harness = await StartHarnessAsync();

        var initial = await ReadData(await harness.Client.GetAsync("/api/metadata/bangumi/oauth"));
        Assert.True(initial.GetProperty("configured").GetBoolean());
        Assert.False(initial.GetProperty("loggedIn").GetBoolean());
        Assert.Equal("idle", initial.GetProperty("state").GetString());

        using var put = await harness.Client.PutAsJsonAsync("/api/settings/metadata/oauth", new
        {
            clientId = "updated-client-id",
            clientSecret = "updated-client-secret",
        });
        var putText = await put.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain("updated-client-secret", putText, StringComparison.Ordinal);
        Assert.DoesNotContain("updated-client-id", putText, StringComparison.Ordinal);
        Assert.Equal("updated-client-secret", harness.Credentials.Load()?.ClientSecret);

        var settings = await ReadData(await harness.Client.GetAsync("/api/settings/metadata/oauth"));
        Assert.True(settings.GetProperty("configured").GetBoolean());
        Assert.Equal("settings", settings.GetProperty("source").GetString());

        await harness.Client.DeleteAsync("/api/settings/metadata/oauth");
        var cleared = await ReadData(await harness.Client.GetAsync("/api/settings/metadata/oauth"));
        Assert.False(cleared.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task OAuthStartReturnsAuthorizeUrlAndSuccessUpdatesStatus()
    {
        await using var harness = await StartHarnessAsync();

        var start = await ReadData(await harness.Client.PostAsync("/api/metadata/bangumi/oauth/start", null));
        var authorizeUrl = start.GetProperty("authorizeUrl").GetString();
        Assert.NotNull(authorizeUrl);
        Assert.Contains("client_id=client-id", authorizeUrl!, StringComparison.Ordinal);
        var state = BangumiOAuthConstants.QueryParameter(authorizeUrl!, "state");
        Assert.False(string.IsNullOrEmpty(state));

        var accepted = await harness.Coordinator.SubmitCallbackUrlAsync(
            harness.Coordinator.CallbackUrl + "?code=code-1&state=" + Uri.EscapeDataString(state!));
        Assert.True(accepted);

        var status = await ReadData(await harness.Client.GetAsync("/api/metadata/bangumi/oauth"));
        Assert.Equal("success", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("loggedIn").GetBoolean());
        Assert.True(status.GetProperty("expiresAtMillis").GetInt64() > 0);
        Assert.Equal("new-access", harness.Tokens.Load().Bangumi);

        var metadata = await ReadData(await harness.Client.GetAsync("/api/settings/metadata"));
        Assert.True(metadata.GetProperty("bangumiOAuthConfigured").GetBoolean());
        Assert.Equal(harness.Tokens.Load().BangumiExpiresAtMillis, metadata.GetProperty("bangumiTokenExpiresAtMillis").GetInt64());
    }

    [Fact]
    public async Task PasswordLoginRoutesIssueCaptchaSaveTokensAndNeverEchoPassword()
    {
        await using var harness = await StartHarnessAsync();
        const string password = "super-secret-password";

        var start = await ReadData(await harness.Client.PostAsync("/api/metadata/bangumi/login/start", null));
        Assert.Equal(BangumiWebConsoleLogin.Challenge, start.GetProperty("status").GetString());
        Assert.StartsWith("data:image/gif;base64,", start.GetProperty("captchaImage").GetString(), StringComparison.Ordinal);

        using var submit = await harness.Client.PostAsJsonAsync("/api/metadata/bangumi/login", new
        {
            email = "user@example.test",
            password,
            captcha = "1234",
        });
        var submitText = await submit.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.DoesNotContain(password, submitText, StringComparison.Ordinal);
        var result = JsonDocument.Parse(submitText).RootElement.GetProperty("data");
        Assert.Equal(BangumiWebConsoleLogin.Success, result.GetProperty("status").GetString());
        var tokens = harness.Tokens.Load();
        Assert.Equal("new-access", tokens.Bangumi);
        Assert.Equal("new-refresh", tokens.BangumiRefreshToken);
        var logs = File.Exists(harness.LogPath) ? await File.ReadAllTextAsync(harness.LogPath) : string.Empty;
        Assert.DoesNotContain(password, logs, StringComparison.Ordinal);

        var manual = await harness.Client.PutAsJsonAsync("/api/settings/metadata/bangumi-token", new { token = "manual-token" });
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var manualTokens = harness.Tokens.Load();
        Assert.Equal("manual-token", manualTokens.Bangumi);
        Assert.Null(manualTokens.BangumiRefreshToken);
        Assert.Equal(0, manualTokens.BangumiExpiresAtMillis);
    }

    [Fact]
    public async Task UnconfiguredOAuthRejectsPasswordLoginBeforeItIsSubmitted()
    {
        await using var harness = await StartHarnessAsync(oauthConfigured: false);

        var start = await ReadData(await harness.Client.PostAsync("/api/metadata/bangumi/login/start", null));

        Assert.Equal(BangumiWebConsoleLogin.NotConfigured, start.GetProperty("status").GetString());
        Assert.Equal(0, harness.PasswordFactoryCalls());
    }

    [Fact]
    public async Task PasswordLoginRejectsEmptyFields()
    {
        await using var harness = await StartHarnessAsync();
        await harness.Client.PostAsync("/api/metadata/bangumi/login/start", null);

        using var response = await harness.Client.PostAsJsonAsync("/api/metadata/bangumi/login", new
        {
            email = "",
            password = "",
            captcha = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WebControlStartFlowTimesOutReleasesListenerAndCanRestart()
    {
        await using var harness = await StartHarnessAsync(oauthTimeout: TimeSpan.FromMilliseconds(150));

        var started = await ReadData(await harness.Client.PostAsync("/api/metadata/bangumi/oauth/start", null));
        Assert.Equal("authorizing", started.GetProperty("state").GetString());
        Assert.NotNull(harness.LastLoopback);

        var state = await WaitForOAuthStateAsync(harness, value => value != "authorizing");
        Assert.Equal("failed", state);
        Assert.True(harness.LastLoopback!.Closed);
        var status = await ReadData(await harness.Client.GetAsync("/api/metadata/bangumi/oauth"));
        Assert.Contains("超时", status.GetProperty("message").GetString(), StringComparison.Ordinal);

        var restarted = await ReadData(await harness.Client.PostAsync("/api/metadata/bangumi/oauth/start", null));
        Assert.Equal("authorizing", restarted.GetProperty("state").GetString());
        Assert.NotNull(restarted.GetProperty("authorizeUrl").GetString());
    }

    private static async Task<string?> WaitForOAuthStateAsync(Harness harness, Func<string?, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        string? state = null;
        while (DateTime.UtcNow < deadline)
        {
            var data = await ReadData(await harness.Client.GetAsync("/api/metadata/bangumi/oauth"));
            state = data.GetProperty("state").GetString();
            if (predicate(state)) return state;
            await Task.Delay(25);
        }
        throw new TimeoutException("OAuth state did not change: " + state);
    }

    private async Task<Harness> StartHarnessAsync(bool oauthConfigured = true, TimeSpan? oauthTimeout = null)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = ReservePort();
        var tokens = new MetadataTokenStore(Path.Combine(directory, "metadata-tokens.bin"));
        var credentials = new BangumiOAuthCredentialStore(Path.Combine(directory, "bangumi-oauth.bin"));
        if (oauthConfigured) credentials.Save("client-id", "client-secret");
        PendingLoopback? lastLoopback = null;
        var coordinator = new BangumiOAuthCoordinator(
            tokens,
            () => BangumiOAuthCredentialsProvider.Resolve(credentials, _ => null),
            () => new TokenHandler(),
            callbackPort: port,
            authorizationTimeout: oauthTimeout,
            loopbackFactory: (_, _) =>
            {
                lastLoopback = new PendingLoopback();
                return lastLoopback;
            });
        var passwordClient = new FakePasswordClient();
        var factoryCalls = 0;
        var webLogin = new BangumiWebConsoleLogin(
            coordinator,
            () => { factoryCalls++; return passwordClient; },
            () => !string.IsNullOrEmpty(tokens.Load().Bangumi),
            () => tokens.Load().BangumiExpiresAtMillis);
        var webTokens = new WebControlTokenStore(Path.Combine(directory, "web-token.bin"));
        var logPath = Path.Combine(directory, "logs", "miruplay.jsonl");
        var settings = new AppSettings();
        var tasks = new HeadlessTaskScheduler(storePath: Path.Combine(directory, "tasks.json"));
        var server = new WebControlServer(
            port,
            webTokens,
            () => [],
            () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false),
            _ => Task.FromResult(new PlaybackRuntimeStatus()),
            () => settings,
            update => { settings = update(settings); return Task.FromResult(settings); },
            new MediaSourceActions(
                () => [],
                _ => Task.FromResult(new SourceTestResponse(true, "ok")),
                request => Task.FromResult(new MediaSourceInfoDto(1, request.Name, request.Type, request.ContentMode, new Dictionary<string, string>(), true, 0)),
                (_, request) => Task.FromResult(new MediaSourceInfoDto(1, request.Name, request.Type, request.ContentMode, new Dictionary<string, string>(), true, 0)),
                _ => Task.CompletedTask,
                (id, _) => Task.FromResult(new SourceScanResponse(id, "source", 0, 0, 0))),
            listenOnAnyIp: false,
            metadataTokens: tokens,
            bangumiArchive: new BangumiArchiveStore(Path.Combine(directory, "bangumi-archive")),
            localLogs: new RotatingLocalLogStore(logPath),
            tasks: tasks,
            bangumiOAuth: coordinator,
            bangumiOAuthCredentials: credentials,
            bangumiWebLogin: webLogin,
            getBangumiToken: cancellationToken => coordinator.EnsureValidTokenAsync(cancellationToken));
        await server.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", webTokens.AccessToken);
        return new Harness(
            server, client, tokens, credentials, coordinator, webLogin, tasks, port, logPath, () => factoryCalls, () => lastLoopback);
    }

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class PendingLoopback : IBangumiOAuthLoopback
    {
        private readonly TaskCompletionSource<string> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Prefix => "http://127.0.0.1:0/";
        public bool Closed { get; private set; }
        public void Start() { }
        public Task<string> WaitForCallbackAsync(CancellationToken cancellationToken) => _callback.Task.WaitAsync(cancellationToken);
        public void Close() => Closed = true;
        public void Dispose() => Close();
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OAuthTokenJson, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakePasswordClient : IBangumiPasswordLoginApi
    {
        public Task<BangumiPasswordLoginStep> StartChallengeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<BangumiPasswordLoginStep>(new BangumiPasswordLoginStep.Challenge([0x47, 0x49, 0x46], "image/gif"));

        public Task<BangumiPasswordLoginStep> RefreshCaptchaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<BangumiPasswordLoginStep>(new BangumiPasswordLoginStep.Challenge([0x47, 0x49, 0x46], "image/gif"));

        public Task<BangumiPasswordLoginStep> LoginAndAuthorizeAsync(
            string email,
            string password,
            string captcha,
            string clientId,
            string redirectUri,
            string state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<BangumiPasswordLoginStep>(new BangumiPasswordLoginStep.Authorized(
                redirectUri + "?code=code-1&state=" + Uri.EscapeDataString(state)));

        public void Dispose() { }
    }

    private sealed class Harness(
        WebControlServer server,
        HttpClient client,
        MetadataTokenStore tokens,
        BangumiOAuthCredentialStore credentials,
        BangumiOAuthCoordinator coordinator,
        BangumiWebConsoleLogin webLogin,
        HeadlessTaskScheduler tasks,
        int port,
        string logPath,
        Func<int> passwordFactoryCalls,
        Func<PendingLoopback?> lastLoopback) : IAsyncDisposable
    {
        public WebControlServer Server => server;
        public HttpClient Client => client;
        public MetadataTokenStore Tokens => tokens;
        public BangumiOAuthCredentialStore Credentials => credentials;
        public BangumiOAuthCoordinator Coordinator => coordinator;
        public int Port => port;
        public string LogPath => logPath;
        public Func<int> PasswordFactoryCalls => passwordFactoryCalls;
        public PendingLoopback? LastLoopback => lastLoopback();

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await server.DisposeAsync();
            webLogin.Dispose();
            coordinator.Dispose();
            await tasks.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
