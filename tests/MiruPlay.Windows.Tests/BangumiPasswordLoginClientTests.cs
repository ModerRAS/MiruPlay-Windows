using System.Net;
using System.Net.Http;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiPasswordLoginClientTests
{
    private const string CallbackUrl = "http://127.0.0.1:13712/callback";

    [Fact]
    public async Task StartChallengeParsesLoginFormAndFetchesCaptcha()
    {
        var handler = new FakeBangumiHandler();
        using var client = CreateClient(handler);

        var step = await client.StartChallengeAsync();

        var challenge = Assert.IsType<BangumiPasswordLoginStep.Challenge>(step);
        Assert.Equal("image/gif", challenge.ContentType);
        Assert.Equal([0x47, 0x49, 0x46], challenge.Image);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/login", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/signup/captcha", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.False(string.IsNullOrEmpty(handler.Requests[1].RequestUri!.Query));
    }

    [Fact]
    public async Task BlankAuthorizeFormActionPostsBackToAuthorizePageUrl()
    {
        var handler = new FakeBangumiHandler();
        using var client = CreateClient(handler);
        await client.StartChallengeAsync();

        var step = await client.LoginAndAuthorizeAsync(
            "user@example.test", "password-secret", "1234", "client-id", CallbackUrl, "state-1");

        var authorized = Assert.IsType<BangumiPasswordLoginStep.Authorized>(step);
        Assert.Contains("code=code-1", authorized.CallbackUrl, StringComparison.Ordinal);
        Assert.Contains("state=state-1", authorized.CallbackUrl, StringComparison.Ordinal);
        var authorizeGet = Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/oauth/authorize");
        var authorizePost = Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/oauth/authorize");
        // Android #100 regression: the empty-action form must be submitted to the full authorize URL
        // (query string with client_id/redirect_uri/state), not the bare endpoint.
        Assert.Equal(authorizeGet.RequestUri!.AbsoluteUri, authorizePost.RequestUri!.AbsoluteUri);
        Assert.Contains("client_id=client-id", authorizePost.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("response_type=code", authorizePost.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("state=state-1", authorizePost.RequestUri!.Query, StringComparison.Ordinal);
        var body = handler.Bodies[handler.Requests.IndexOf(authorizePost)];
        Assert.Contains("formhash=hash-2", body, StringComparison.Ordinal);
        // Password only appears in the login POST body, never in URLs; the loopback callback
        // is intercepted from the redirect, never fetched through the (possibly proxied) client.
        Assert.All(handler.Requests, request =>
            Assert.DoesNotContain("password-secret", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal));
        Assert.All(handler.Requests, request =>
            Assert.Equal("bgm.test", request.RequestUri!.Host));
    }

    [Fact]
    public async Task ExplicitAuthorizeFormActionIsResolvedAgainstPageUrl()
    {
        var handler = new FakeBangumiHandler { AuthorizeAction = "/oauth/authorize" };
        using var client = CreateClient(handler);
        await client.StartChallengeAsync();

        var step = await client.LoginAndAuthorizeAsync("user@example.test", "password", "1234", "client-id", CallbackUrl, "state-1");

        Assert.IsType<BangumiPasswordLoginStep.Authorized>(step);
        var authorizePost = Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/oauth/authorize");
        Assert.Equal("https://bgm.test/oauth/authorize", authorizePost.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task WrongCaptchaReturnsRetryWithNewCaptchaAndKeepsSession()
    {
        var handler = new FakeBangumiHandler { LoginSucceeds = false };
        using var client = CreateClient(handler);
        await client.StartChallengeAsync();

        var retryStep = await client.LoginAndAuthorizeAsync("user@example.test", "password", "bad", "client-id", CallbackUrl, "state-1");

        var retry = Assert.IsType<BangumiPasswordLoginStep.Retry>(retryStep);
        Assert.Contains("不正确", retry.Message, StringComparison.Ordinal);
        Assert.NotEmpty(retry.Image);
        handler.LoginSucceeds = true;
        var secondStep = await client.LoginAndAuthorizeAsync("user@example.test", "password", "good", "client-id", CallbackUrl, "state-1");
        Assert.IsType<BangumiPasswordLoginStep.Authorized>(secondStep);
        var loginPosts = handler.Requests.Where(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/FollowTheRabbit").ToList();
        Assert.Equal(2, loginPosts.Count);
        var secondBody = handler.Bodies[handler.Requests.IndexOf(loginPosts[1])];
        Assert.Contains("formhash=hash-1", secondBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BangumiCannotLoginNoticeIsTerminalFailure()
    {
        var handler = new FakeBangumiHandler { LoginSucceeds = false, LoginNotice = "该账号不能登录。" };
        using var client = CreateClient(handler);
        await client.StartChallengeAsync();

        var step = await client.LoginAndAuthorizeAsync("user@example.test", "password", "bad", "client-id", CallbackUrl, "state-1");

        var failed = Assert.IsType<BangumiPasswordLoginStep.Failed>(step);
        Assert.Contains("不能登录", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLoginFormFailsClearly()
    {
        var handler = new FakeBangumiHandler { LoginPageHtml = "<html><body>no form</body></html>" };
        using var client = CreateClient(handler);

        var step = await client.StartChallengeAsync();

        var failed = Assert.IsType<BangumiPasswordLoginStep.Failed>(step);
        Assert.Equal(AppStrings.Get("bgmOauth.loginFormMissing"), failed.Message);
    }

    [Fact]
    public async Task RefreshCaptchaRequiresChallengeAndRefreshesImage()
    {
        var handler = new FakeBangumiHandler();
        using var client = CreateClient(handler);
        var before = await client.RefreshCaptchaAsync();
        Assert.IsType<BangumiPasswordLoginStep.Failed>(before);

        await client.StartChallengeAsync();
        var refreshed = await client.RefreshCaptchaAsync();
        var challenge = Assert.IsType<BangumiPasswordLoginStep.Challenge>(refreshed);
        Assert.NotEmpty(challenge.Image);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CallbackWithoutStateGetsStateAppended()
    {
        var handler = new FakeBangumiHandler { CallbackIncludesState = false };
        using var client = CreateClient(handler);
        await client.StartChallengeAsync();

        var step = await client.LoginAndAuthorizeAsync("user@example.test", "password", "1234", "client-id", CallbackUrl, "state-1");

        var authorized = Assert.IsType<BangumiPasswordLoginStep.Authorized>(step);
        Assert.Contains("state=state-1", authorized.CallbackUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailureMapsToSafeMessage()
    {
        using var client = new BangumiPasswordLoginClient(
            "https://bgm.test",
            handler: new ThrowingHandler(),
            clockMillis: () => 1,
            captchaSuffix: () => 1);

        var step = await client.StartChallengeAsync();

        var failed = Assert.IsType<BangumiPasswordLoginStep.Failed>(step);
        Assert.Equal(AppStrings.Get("bgmOauth.networkFailed"), failed.Message);
    }

    private static BangumiPasswordLoginClient CreateClient(HttpMessageHandler handler) =>
        new("https://bgm.test", handler: handler, clockMillis: () => 1_000, captchaSuffix: () => 2);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class FakeBangumiHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public bool LoginSucceeds { get; set; } = true;
        public string LoginNotice { get; set; } = "账号、密码或验证码不正确。";
        public string? AuthorizeAction { get; set; }
        public bool CallbackIncludesState { get; set; } = true;
        public string LoginPageHtml { get; set; } = """
            <html><body>
              <form id="loginForm" action="/FollowTheRabbit" method="post">
                <input type="hidden" name="formhash" value="hash-1">
                <input name="email"><input name="password"><input name="captcha_challenge_field">
                <input type="submit" name="loginsubmit" value="登录">
              </form>
            </body></html>
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/login") return Html(LoginPageHtml);
            if (request.Method == HttpMethod.Get && path == "/signup/captcha")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x47, 0x49, 0x46]),
                }.WithContentType("image/gif");
            if (request.Method == HttpMethod.Post && path == "/FollowTheRabbit")
            {
                if (!LoginSucceeds) return Html(NoticeHtml(LoginNotice));
                var response = Redirect("/");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "chii_auth=ok; Path=/");
                return response;
            }
            if (request.Method == HttpMethod.Get && path == "/")
                return Html("<html><body>home</body></html>");
            if (request.Method == HttpMethod.Get && path == "/oauth/authorize")
                return Html(AuthorizeHtml(AuthorizeAction));
            if (request.Method == HttpMethod.Post && path == "/oauth/authorize")
            {
                var state = BangumiOAuthConstants.QueryParameter(request.RequestUri!.AbsoluteUri, "state") ?? string.Empty;
                var location = CallbackIncludesState
                    ? CallbackUrl + "?code=code-1&state=" + Uri.EscapeDataString(state)
                    : CallbackUrl + "?code=code-1";
                return Redirect(location);
            }
            return Html("<html><body>unknown</body></html>");
        }

        private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };

        private static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        }

        private static string NoticeHtml(string notice) =>
            "<html><body><div id=\"colunmNotice\" class=\"message\"><p class=\"text\">" + notice + "</p></div></body></html>";

        private static string AuthorizeHtml(string? action) =>
            "<html><body><form method=\"post\"" + (action is null ? string.Empty : " action=\"" + action + "\"") + ">" +
            "<input type=\"hidden\" name=\"formhash\" value=\"hash-2\">" +
            "<input type=\"hidden\" name=\"client_id\" value=\"client-id\">" +
            "<input type=\"hidden\" name=\"redirect_uri\" value=\"redirect\">" +
            "<input type=\"submit\" name=\"submit\" value=\"授权\">" +
            "</form></body></html>";
    }
}

internal static class HttpResponseMessageTestExtensions
{
    public static HttpResponseMessage WithContentType(this HttpResponseMessage response, string contentType)
    {
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }
}
