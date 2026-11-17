using System.Net;
using System.Net.Http;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiOAuthCoordinatorTests : IDisposable
{
    private const string TokenJson = """
        {"access_token":"new-access","expires_in":604800,"refresh_token":"new-refresh","user_id":42}
        """;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miruplay-oauth-coordinator-" + Guid.NewGuid().ToString("N"));
    private readonly MetadataTokenStore _tokens;
    private readonly int _port;

    public BangumiOAuthCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
        _tokens = new MetadataTokenStore(Path.Combine(_directory, "metadata-tokens.bin"));
        _port = ReservePort();
    }

    [Fact]
    public void UnconfiguredStartReportsNotConfiguredWithoutLoopback()
    {
        var loopback = new FakeLoopback();
        using var coordinator = CreateCoordinator(null, null, () => loopback);

        var url = coordinator.StartAuthorize();

        Assert.Null(url);
        Assert.Equal(BangumiOAuthStatus.NotConfigured, coordinator.State.Status);
        Assert.Equal(0, loopback.StartCount);
    }

    [Fact]
    public void StartBuildsAuthorizeUrlAndIsSingleFlight()
    {
        var loopback = new FakeLoopback();
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("client-id", "client-secret", "settings"),
            () => throw new InvalidOperationException("no network during start"),
            () => loopback);

        var first = coordinator.StartAuthorize();
        var second = coordinator.StartAuthorize();

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, loopback.StartCount);
        Assert.Contains("client_id=client-id", first!, StringComparison.Ordinal);
        Assert.Contains("state=state-1", first!, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString($"http://127.0.0.1:{_port}/callback"), first!, StringComparison.Ordinal);
        Assert.Equal(BangumiOAuthStatus.Authorizing, coordinator.State.Status);
    }

    [Fact]
    public async Task CallbackExchangesCodeAndPersistsRotatedTokens()
    {
        var loopback = new FakeLoopback();
        var handler = new RecordingOAuthHandler(TokenJson);
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("client-id", "client-secret", "settings"),
            () => handler,
            () => loopback,
            clockMillis: () => 1_000_000);
        coordinator.StartAuthorize();

        loopback.Deliver($"http://127.0.0.1:{_port}/callback?code=code-1&state=state-1");
        var result = await coordinator.WaitForResultAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(BangumiOAuthStatus.Success, coordinator.State.Status);
        Assert.True(loopback.Closed);
        var tokens = _tokens.Load();
        Assert.Equal("new-access", tokens.Bangumi);
        Assert.Equal("new-refresh", tokens.BangumiRefreshToken);
        Assert.Equal(1_000_000 + 604_800_000, tokens.BangumiExpiresAtMillis);
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains("code=code-1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongStateIsRejectedAndFlowStaysAuthorizing()
    {
        var loopback = new FakeLoopback();
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => new RecordingOAuthHandler(TokenJson),
            () => loopback);
        coordinator.StartAuthorize();

        var accepted = await coordinator.SubmitCallbackUrlAsync($"http://127.0.0.1:{_port}/callback?code=code-1&state=wrong");

        Assert.False(accepted);
        Assert.Equal(BangumiOAuthStatus.Authorizing, coordinator.State.Status);
        Assert.True(await coordinator.SubmitCallbackUrlAsync($"http://127.0.0.1:{_port}/callback?code=code-1&state=state-1"));
        Assert.Equal(BangumiOAuthStatus.Success, coordinator.State.Status);
    }

    [Fact]
    public async Task CallbackStateIsOneTimeAndMissingCodeFails()
    {
        var loopback = new FakeLoopback();
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => new RecordingOAuthHandler(TokenJson),
            () => loopback);
        coordinator.StartAuthorize();
        var callback = $"http://127.0.0.1:{_port}/callback?code=code-1&state=state-1";
        Assert.True(await coordinator.SubmitCallbackUrlAsync(callback));
        Assert.False(await coordinator.SubmitCallbackUrlAsync(callback));

        using var second = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => new RecordingOAuthHandler(TokenJson),
            () => new FakeLoopback());
        second.StartAuthorize();
        Assert.True(await second.SubmitCallbackUrlAsync($"http://127.0.0.1:{_port}/callback?state=state-1"));
        Assert.Equal(BangumiOAuthStatus.Failed, second.State.Status);
        Assert.Equal(AppStrings.Get("bgmOauth.missingCode"), second.State.Message);
    }

    [Fact]
    public async Task AuthorizationTimesOutAfterConfiguredWindow()
    {
        var loopback = new FakeLoopback();
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => new RecordingOAuthHandler(TokenJson),
            () => loopback,
            timeout: TimeSpan.FromMilliseconds(150));
        coordinator.StartAuthorize();

        var result = await coordinator.WaitForResultAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(BangumiOAuthStatus.Failed, result.State.Status);
        Assert.Equal(AppStrings.Get("bgmOauth.timeout"), result.Error);
        Assert.True(loopback.Closed);
        Assert.Equal(TimeSpan.FromMinutes(10), BangumiOAuthCoordinator.DefaultAuthorizationTimeout);
        Assert.Equal(13712, BangumiOAuthConstants.DefaultCallbackPort);
    }

    [Fact]
    public void CancelStopsLoopbackAndResetsState()
    {
        var loopback = new FakeLoopback();
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => new RecordingOAuthHandler(TokenJson),
            () => loopback);
        coordinator.StartAuthorize();

        coordinator.Cancel();

        Assert.Equal(BangumiOAuthStatus.Idle, coordinator.State.Status);
        Assert.True(loopback.Closed);
    }

    [Fact]
    public void PortInUseSurfacesExplicitFailure()
    {
        var loopback = new FakeLoopback { StartError = new InvalidOperationException(AppStrings.Get("bgmOauth.portBusy", _port)) };
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => throw new InvalidOperationException("no network expected"),
            () => loopback);

        var error = Assert.Throws<InvalidOperationException>(coordinator.StartAuthorize);

        Assert.Contains(_port.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Equal(BangumiOAuthStatus.Failed, coordinator.State.Status);
    }

    [Fact]
    public async Task EnsureValidTokenRefreshesInsideMarginAndRotatesRefreshToken()
    {
        _tokens.SaveBangumiSession("old-access", "old-refresh", 1_000_000 + 60L * 60L * 1000L);
        var handler = new RecordingOAuthHandler(TokenJson);
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => handler,
            () => new FakeLoopback(),
            clockMillis: () => 1_000_000);

        var token = await coordinator.EnsureValidTokenAsync();

        Assert.Equal("new-access", token);
        Assert.Single(handler.Requests);
        var tokens = _tokens.Load();
        Assert.Equal("new-refresh", tokens.BangumiRefreshToken);
        Assert.Equal(1_000_000 + 604_800_000, tokens.BangumiExpiresAtMillis);
    }

    [Fact]
    public async Task EnsureValidTokenKeepsFreshTokenWithoutNetwork()
    {
        _tokens.SaveBangumiSession("fresh-access", "fresh-refresh", 1_000_000 + 48L * 60L * 60L * 1000L);
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => throw new InvalidOperationException("network must not be used"),
            () => new FakeLoopback(),
            clockMillis: () => 1_000_000);

        Assert.Equal("fresh-access", await coordinator.EnsureValidTokenAsync());
    }

    [Fact]
    public async Task EnsureValidTokenReturnsNullWhenLoggedOutAndManualTokensStayUsable()
    {
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => throw new InvalidOperationException("network must not be used"),
            () => new FakeLoopback());
        Assert.Null(await coordinator.EnsureValidTokenAsync());

        _tokens.SaveBangumi("manual-token");
        Assert.Equal("manual-token", await coordinator.EnsureValidTokenAsync());
    }

    [Fact]
    public void LogoutClearsAccessRefreshAndExpiry()
    {
        _tokens.SaveBangumiSession("access", "refresh", 123_456);
        using var coordinator = CreateCoordinator(
            new BangumiOAuthCredentials("id", "secret", "settings"),
            () => new RecordingOAuthHandler(TokenJson),
            () => new FakeLoopback());

        coordinator.Logout();

        var tokens = _tokens.Load();
        Assert.Null(tokens.Bangumi);
        Assert.Null(tokens.BangumiRefreshToken);
        Assert.Equal(0, tokens.BangumiExpiresAtMillis);
    }

    [Fact]
    public async Task RefreshSchedulerRunsOncePerIntervalAndStopsCleanly()
    {
        var flow = new FakeFlow { ExpiresAtMillis = 123 };
        var scheduler = new BangumiOAuthRefreshScheduler(flow);

        Assert.True(await scheduler.RunIfDueAsync(1_000_000));
        Assert.False(await scheduler.RunIfDueAsync(1_000_001));
        Assert.Equal(1, flow.EnsureCalls);
        scheduler.Start();
        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task RefreshSchedulerSkipsUnconfiguredOrLoggedOutFlows()
    {
        var unconfigured = new FakeFlow { IsConfigured = false, ExpiresAtMillis = 1 };
        await using var first = new BangumiOAuthRefreshScheduler(unconfigured);
        Assert.False(await first.RunIfDueAsync(1_000_000));
        Assert.Equal(0, unconfigured.EnsureCalls);

        var loggedOut = new FakeFlow { ExpiresAtMillis = 0 };
        await using var second = new BangumiOAuthRefreshScheduler(loggedOut);
        Assert.False(await second.RunIfDueAsync(1_000_000));
        Assert.Equal(0, loggedOut.EnsureCalls);
    }

    private BangumiOAuthCoordinator CreateCoordinator(
        BangumiOAuthCredentials? credentials,
        Func<HttpMessageHandler>? handlerFactory,
        Func<FakeLoopback> loopbackFactory,
        Func<long>? clockMillis = null,
        TimeSpan? timeout = null) =>
        new(
            _tokens,
            () => credentials,
            handlerFactory,
            clockMillis,
            _port,
            timeout,
            (_, _) => loopbackFactory(),
            () => "state-1");

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeLoopback : IBangumiOAuthLoopback
    {
        private readonly TaskCompletionSource<string> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount { get; private set; }
        public bool Closed { get; private set; }
        public Exception? StartError { get; init; }
        public string Prefix => "http://127.0.0.1:0/";
        public void Start()
        {
            StartCount++;
            if (StartError is not null) throw StartError;
        }
        public Task<string> WaitForCallbackAsync(CancellationToken cancellationToken) => _callback.Task.WaitAsync(cancellationToken);
        public void Deliver(string callbackUrl) => _callback.TrySetResult(callbackUrl);
        public void Close()
        {
            Closed = true;
            _callback.TrySetCanceled();
        }
        public void Dispose() => Close();
    }

    private sealed class RecordingOAuthHandler(string tokenJson) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeFlow : IBangumiOAuthFlow
    {
        public bool IsConfigured { get; set; } = true;
        public BangumiOAuthState State { get; set; } = new(BangumiOAuthStatus.Idle);
        public long ExpiresAtMillis { get; set; }
        public string CallbackUrl => "http://127.0.0.1:13712/callback";
        public int EnsureCalls { get; private set; }
        public string? StartAuthorize() => null;
        public Task<BangumiOAuthResult> WaitForResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BangumiOAuthResult(false, State));
        public Task<bool> SubmitCallbackUrlAsync(string callbackUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void Cancel() { }
        public void Logout() { }
        public Task<string?> EnsureValidTokenAsync(CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            return Task.FromResult<string?>("token");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
