using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiWebConsoleLoginTests
{
    private const string CallbackUrl = "http://127.0.0.1:13712/callback";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StartWithoutOAuthConfigurationDoesNotCreatePasswordClient()
    {
        var flow = new FakeOAuthFlow { IsConfigured = false };
        var factoryCalls = 0;
        using var login = new BangumiWebConsoleLogin(
            flow,
            () => { factoryCalls++; return new FakePasswordClient(); },
            () => false);

        var dto = await login.StartAsync();

        Assert.Equal(BangumiWebConsoleLogin.NotConfigured, dto.Status);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task StartReturnsChallengeAsDataUrl()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient { StartStep = new BangumiPasswordLoginStep.Challenge([0x47, 0x49, 0x46], "image/gif") };
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false);

        var dto = await login.StartAsync();

        Assert.Equal(BangumiWebConsoleLogin.Challenge, dto.Status);
        Assert.StartsWith("data:image/gif;base64,", dto.CaptchaImage, StringComparison.Ordinal);
        Assert.True(flow.State.Status == BangumiOAuthStatus.Authorizing);
    }

    [Fact]
    public async Task RefreshCaptchaUpdatesChallengeImage()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient
        {
            StartStep = new BangumiPasswordLoginStep.Challenge([1], "image/gif"),
            RefreshStep = new BangumiPasswordLoginStep.Challenge([2], "image/png"),
        };
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false);
        await login.StartAsync();

        var dto = await login.RefreshCaptchaAsync();

        Assert.Equal(BangumiWebConsoleLogin.Challenge, dto.Status);
        Assert.StartsWith("data:image/png;base64,", dto.CaptchaImage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitSuccessExchangesCallbackAndClearsSession()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient();
        using var login = new BangumiWebConsoleLogin(
            flow,
            () => client,
            () => true,
            () => 4_000_000,
            () => 1_000_000);
        await login.StartAsync();

        var dto = await login.SubmitAsync("user@example.test", "password-secret", "1234");

        Assert.Equal(BangumiWebConsoleLogin.Success, dto.Status);
        Assert.True(dto.TokenConfigured);
        Assert.Equal(4_000_000, dto.ExpiresAtMillis);
        Assert.Equal(1, flow.SubmitCalls);
        Assert.Equal(BangumiOAuthStatus.Success, flow.State.Status);
        var json = JsonSerializer.Serialize(dto, WebJsonOptions);
        Assert.DoesNotContain("password-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        var second = await login.SubmitAsync("user@example.test", "password-secret", "1234");
        Assert.Equal(BangumiWebConsoleLogin.Failed, second.Status);
        Assert.Equal(1, client.LoginCalls);
    }

    [Fact]
    public async Task RetryKeepsSessionAndReturnsFreshCaptcha()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient
        {
            LoginStep = new BangumiPasswordLoginStep.Retry("bad captcha", [2], "image/gif"),
        };
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false);
        await login.StartAsync();

        var retry = await login.SubmitAsync("user@example.test", "password", "bad");

        Assert.Equal(BangumiWebConsoleLogin.Challenge, retry.Status);
        Assert.Equal("bad captcha", retry.Message);
        Assert.StartsWith("data:image/gif;base64,", retry.CaptchaImage, StringComparison.Ordinal);
        client.LoginStep = null;
        var success = await login.SubmitAsync("user@example.test", "password", "good");
        Assert.Equal(BangumiWebConsoleLogin.Success, success.Status);
        Assert.Equal(2, client.LoginCalls);
    }

    [Fact]
    public async Task ExpiredSessionIsDiscardedBeforePasswordIsPosted()
    {
        var clock = 0L;
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient();
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false, clockMillis: () => clock);
        await login.StartAsync();
        clock = BangumiWebConsoleLogin.SessionTtlMillis + 1;

        var dto = await login.SubmitAsync("user@example.test", "password", "1234");

        Assert.Equal(BangumiWebConsoleLogin.Failed, dto.Status);
        Assert.Equal(AppStrings.Get("bgmOauth.loginExpired"), dto.Message);
        Assert.Equal(0, client.LoginCalls);
        Assert.True(flow.CancelCalls >= 1);
        Assert.Equal(10L * 60L * 1000L, BangumiWebConsoleLogin.SessionTtlMillis);
    }

    [Fact]
    public async Task UnconfiguredFlowRejectsBeforePasswordSubmission()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient();
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false);
        await login.StartAsync();
        flow.IsConfigured = false;

        var dto = await login.SubmitAsync("user@example.test", "password", "1234");

        Assert.Equal(BangumiWebConsoleLogin.NotConfigured, dto.Status);
        Assert.Equal(0, client.LoginCalls);
    }

    [Fact]
    public async Task ValidationRejectsEmptyOrOverlongFields()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient();
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false);

        await Assert.ThrowsAsync<ArgumentException>(() => login.SubmitAsync("", "password", "1234"));
        await Assert.ThrowsAsync<ArgumentException>(() => login.SubmitAsync("mail", "", "1234"));
        await Assert.ThrowsAsync<ArgumentException>(() => login.SubmitAsync("mail", "password", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => login.SubmitAsync(new string('m', 321), "password", "1234"));
    }

    [Fact]
    public async Task CancelAndLogoutDropSessionAndDelegateToFlow()
    {
        var flow = new FakeOAuthFlow();
        var client = new FakePasswordClient();
        using var login = new BangumiWebConsoleLogin(flow, () => client, () => false);
        await login.StartAsync();

        var cancelled = await login.CancelAsync();
        Assert.Equal(BangumiWebConsoleLogin.Idle, cancelled.Status);
        Assert.Equal(1, flow.CancelCalls);
        Assert.True(client.Disposed);

        var relogin = await login.StartAsync();
        Assert.Equal(BangumiWebConsoleLogin.Challenge, relogin.Status);
        var loggedOut = await login.LogoutAsync();
        Assert.Equal(BangumiWebConsoleLogin.Idle, loggedOut.Status);
        Assert.Equal(1, flow.LogoutCalls);
    }

    private sealed class FakeOAuthFlow : IBangumiOAuthFlow
    {
        public bool IsConfigured { get; set; } = true;
        public BangumiOAuthState State { get; set; } = new(BangumiOAuthStatus.Idle);
        public long ExpiresAtMillis { get; set; }
        public string CallbackUrl => CallbackUrlValue;
        public int SubmitCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int LogoutCalls { get; private set; }
        private static readonly string CallbackUrlValue = BangumiWebConsoleLoginTests.CallbackUrl;

        public string? StartAuthorize()
        {
            if (!IsConfigured)
            {
                State = new BangumiOAuthState(BangumiOAuthStatus.NotConfigured, Message: "not configured");
                return null;
            }
            var url = BangumiOAuthConstants.BuildAuthorizeUrl("client-id", "state-1", CallbackUrlValue);
            State = new BangumiOAuthState(BangumiOAuthStatus.Authorizing, url);
            return url;
        }

        public Task<BangumiOAuthResult> WaitForResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BangumiOAuthResult(false, State));

        public Task<bool> SubmitCallbackUrlAsync(string callbackUrl, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            State = new BangumiOAuthState(BangumiOAuthStatus.Success, State.AuthorizeUrl);
            return Task.FromResult(true);
        }

        public void Cancel()
        {
            CancelCalls++;
            State = new BangumiOAuthState(BangumiOAuthStatus.Idle);
        }

        public void Logout()
        {
            LogoutCalls++;
            State = new BangumiOAuthState(BangumiOAuthStatus.Idle);
        }

        public Task<string?> EnsureValidTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakePasswordClient : IBangumiPasswordLoginApi
    {
        public BangumiPasswordLoginStep StartStep { get; set; } = new BangumiPasswordLoginStep.Challenge([1], "image/gif");
        public BangumiPasswordLoginStep RefreshStep { get; set; } = new BangumiPasswordLoginStep.Challenge([1], "image/gif");
        public BangumiPasswordLoginStep? LoginStep { get; set; }
        public int LoginCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<BangumiPasswordLoginStep> StartChallengeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StartStep);

        public Task<BangumiPasswordLoginStep> RefreshCaptchaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshStep);

        public Task<BangumiPasswordLoginStep> LoginAndAuthorizeAsync(
            string email,
            string password,
            string captcha,
            string clientId,
            string redirectUri,
            string state,
            CancellationToken cancellationToken = default)
        {
            LoginCalls++;
            var step = LoginStep ?? new BangumiPasswordLoginStep.Authorized(
                redirectUri + "?code=code-1&state=" + Uri.EscapeDataString(state));
            return Task.FromResult(step);
        }

        public void Dispose() => Disposed = true;
    }
}
