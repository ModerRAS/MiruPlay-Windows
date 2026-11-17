using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public interface IBangumiPasswordLoginApi : IDisposable
{
    Task<BangumiPasswordLoginStep> StartChallengeAsync(CancellationToken cancellationToken = default);
    Task<BangumiPasswordLoginStep> RefreshCaptchaAsync(CancellationToken cancellationToken = default);
    Task<BangumiPasswordLoginStep> LoginAndAuthorizeAsync(
        string email,
        string password,
        string captcha,
        string clientId,
        string redirectUri,
        string state,
        CancellationToken cancellationToken = default);
}

public abstract record BangumiPasswordLoginStep
{
    public sealed record Challenge(byte[] Image, string ContentType) : BangumiPasswordLoginStep;

    public sealed record Retry(string Message, byte[] Image, string ContentType) : BangumiPasswordLoginStep;

    public sealed record Authorized(string CallbackUrl) : BangumiPasswordLoginStep;

    public sealed record Failed(string Message) : BangumiPasswordLoginStep;
}

/// <summary>
/// Server-side Bangumi login used by the web console: fetches the login form + captcha,
/// submits email/password/captcha once, then walks the OAuth authorize form inside the same
/// cookie session. The password only ever lives in the one form body of the login POST; it
/// is not stored, returned, logged, or included in exception messages.
/// </summary>
public sealed class BangumiPasswordLoginClient : IBangumiPasswordLoginApi, IDisposable
{
    public const string DefaultBaseUrl = "https://bgm.tv";
    private const int MaxRedirects = 5;
    private const int MaxBodyBytes = 2_000_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Regex InputTag = new("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex FormTag = new("<form\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex AttributePattern = new(
        "([A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex MessageBlock = new(
        "<div\\b[^>]*class\\s*=\\s*[\"'][^\"']*message[^\"']*[\"'][^>]*>([\\s\\S]*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex NoticeParagraph = new(
        "<p\\b[^>]*class\\s*=\\s*[\"'][^\"']*text[^\"']*[\"'][^>]*>([\\s\\S]*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookies = new();
    private readonly Uri _baseUri;
    private readonly string _userAgent;
    private readonly Func<long> _clockMillis;
    private readonly Func<int> _captchaSuffix;
    private string _loginAction = "/FollowTheRabbit";
    private string _formhash = string.Empty;

    public BangumiPasswordLoginClient(
        string baseUrl = DefaultBaseUrl,
        string userAgent = BangumiOAuthConstants.UserAgent,
        HttpMessageHandler? handler = null,
        Func<long>? clockMillis = null,
        Func<int>? captchaSuffix = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _userAgent = userAgent;
        _clockMillis = clockMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _captchaSuffix = captchaSuffix ?? (() => Random.Shared.Next(1, 7));
        _httpClient = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<BangumiPasswordLoginStep> StartChallengeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadLoginFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UriFormatException)
        {
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.networkFailed"));
        }
    }

    public async Task<BangumiPasswordLoginStep> RefreshCaptchaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_formhash)) return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.needChallenge"));
            return await FetchCaptchaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UriFormatException)
        {
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.networkFailed"));
        }
    }

    public async Task<BangumiPasswordLoginStep> LoginAndAuthorizeAsync(
        string email,
        string password,
        string captcha,
        string clientId,
        string redirectUri,
        string state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_formhash))
                return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.needChallenge"));

            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, Resolve(_loginAction));
            loginRequest.Headers.Referrer = Resolve("/login");
            loginRequest.Headers.TryAddWithoutValidation("Origin", Origin);
            loginRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["formhash"] = _formhash,
                ["referer"] = string.Empty,
                ["dreferer"] = string.Empty,
                ["email"] = email,
                ["password"] = password,
                ["captcha_challenge_field"] = captcha,
                ["loginsubmit"] = "登录",
            });
            var posted = await ExecuteAsync(loginRequest, cancellationToken).ConfigureAwait(false);
            var landed = await FollowSameSiteAsync(posted, redirectUri, cancellationToken).ConfigureAwait(false);
            if (landed.CallbackUrl is not null)
                return new BangumiPasswordLoginStep.Authorized(WithOAuthState(landed.CallbackUrl, state));

            if (!HasCookie("chii_auth"))
            {
                var notice = ParseBangumiNotice(landed.Body) ?? AppStrings.Get("bgmOauth.credentialsInvalid");
                if (notice.Contains("不能登录", StringComparison.Ordinal))
                    return new BangumiPasswordLoginStep.Failed(notice);
                var again = await LoadLoginFormAsync(cancellationToken).ConfigureAwait(false);
                return again switch
                {
                    BangumiPasswordLoginStep.Challenge challenge =>
                        new BangumiPasswordLoginStep.Retry(notice, challenge.Image, challenge.ContentType),
                    BangumiPasswordLoginStep.Failed => new BangumiPasswordLoginStep.Failed(notice),
                    _ => new BangumiPasswordLoginStep.Failed(notice),
                };
            }

            var authorizeUrl = AuthorizeUrl(clientId, redirectUri, state);
            using var authorizeRequest = new HttpRequestMessage(HttpMethod.Get, authorizeUrl);
            authorizeRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            var authorize = await ExecuteAsync(authorizeRequest, cancellationToken).ConfigureAwait(false);
            var authorizeLanded = await FollowSameSiteAsync(authorize, redirectUri, cancellationToken).ConfigureAwait(false);
            if (authorizeLanded.CallbackUrl is not null)
                return new BangumiPasswordLoginStep.Authorized(WithOAuthState(authorizeLanded.CallbackUrl, state));
            if (authorizeLanded.Body.Contains("id=\"loginForm\"", StringComparison.Ordinal) ||
                authorizeLanded.Body.Contains("id='loginForm'", StringComparison.Ordinal))
                return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.loginNotKept"));

            var form = ParseAuthorizeForm(authorizeLanded.Body);
            if (form is null)
                return new BangumiPasswordLoginStep.Failed(
                    ParseBangumiNotice(authorizeLanded.Body) ?? AppStrings.Get("bgmOauth.authFormMissing"));

            // Android #100 fix: Bangumi's authorize form has no action attribute, so a real
            // browser posts back to the current page URL. Posting to a bare /oauth/authorize
            // drops client_id/response_type/redirect_uri/state and Bangumi answers app_nonexistence.
            var actionUrl = form.Action.Length == 0 ? authorizeUrl : ResolveAgainst(authorizeUrl, form.Action);
            using var submitRequest = new HttpRequestMessage(HttpMethod.Post, actionUrl);
            submitRequest.Headers.Referrer = new Uri(authorizeUrl, UriKind.Absolute);
            submitRequest.Headers.TryAddWithoutValidation("Origin", Origin);
            submitRequest.Content = new FormUrlEncodedContent(form.Fields);
            var submitted = await ExecuteAsync(submitRequest, cancellationToken).ConfigureAwait(false);
            var done = await FollowSameSiteAsync(submitted, redirectUri, cancellationToken).ConfigureAwait(false);
            if (done.CallbackUrl is not null)
                return new BangumiPasswordLoginStep.Authorized(WithOAuthState(done.CallbackUrl, state));
            return new BangumiPasswordLoginStep.Failed(ParseBangumiNotice(done.Body) ?? AppStrings.Get("bgmOauth.authNoCode"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UriFormatException)
        {
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.networkFailed"));
        }
    }

    private async Task<BangumiPasswordLoginStep> LoadLoginFormAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve("/login"));
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        var page = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (page.Code is < 200 or > 299)
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.loginPageFailed", page.Code));
        var parsed = ParseLoginForm(page.Body);
        if (parsed is null)
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.loginFormMissing"));
        _loginAction = parsed.Action;
        _formhash = parsed.Formhash;
        return await FetchCaptchaAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<BangumiPasswordLoginStep> FetchCaptchaAsync(CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(Resolve("/signup/captcha"));
        builder.Query = _clockMillis().ToString(CultureInfo.InvariantCulture)
            + Math.Clamp(_captchaSuffix(), 1, 6).ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Referrer = Resolve("/login");
        request.Headers.TryAddWithoutValidation("Accept", "image/gif,image/png,image/*;q=0.8");
        var page = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (page.Code is < 200 or > 299 || page.Bytes.Length == 0)
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.captchaFailed"));
        if (page.Bytes.Length > MaxBodyBytes)
            return new BangumiPasswordLoginStep.Failed(AppStrings.Get("bgmOauth.captchaTooLarge"));
        return new BangumiPasswordLoginStep.Challenge(page.Bytes, ImageContentType(page.ContentType));
    }

    private async Task<HttpResult> ExecuteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        var cookieHeader = _cookies.GetCookieHeader(request.RequestUri!);
        if (cookieHeader.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        StoreCookies(request.RequestUri!, response);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxBodyBytes)
            throw new InvalidDataException(AppStrings.Get("bgmOauth.responseTooLarge"));
        var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        string? location = null;
        if (response.Headers.Location is { } headerLocation)
            location = headerLocation.IsAbsoluteUri
                ? headerLocation.AbsoluteUri
                : new Uri(request.RequestUri!, headerLocation).AbsoluteUri;
        var body = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Encoding.UTF8.GetString(bytes);
        return new HttpResult((int)response.StatusCode, location, body, contentType, bytes);
    }

    private void StoreCookies(Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var value in values)
        {
            try
            {
                _cookies.SetCookies(uri, value.Split(';')[0]);
            }
            catch (CookieException)
            {
            }
        }
    }

    private async Task<Landed> FollowSameSiteAsync(HttpResult initial, string redirectUri, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect < MaxRedirects; redirect++)
        {
            var location = current.Location;
            if (location is null) return new Landed(current.Body, null);
            if (IsOAuthCallback(location, redirectUri)) return new Landed(current.Body, location);
            if (!SameSite(location)) return new Landed(current.Body, null);
            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            current = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return new Landed(current.Body, null);
    }

    private Uri Resolve(string reference) => new(_baseUri, reference);

    private static string ResolveAgainst(string pageUrl, string reference) =>
        new Uri(new Uri(pageUrl, UriKind.Absolute), reference).AbsoluteUri;

    private string Origin => _baseUri.GetLeftPart(UriPartial.Authority);

    private bool SameSite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return false;
        return string.Equals(target.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
            target.Port == _baseUri.Port;
    }

    private bool HasCookie(string name) =>
        _cookies.GetCookies(_baseUri).Cast<Cookie>().Any(cookie =>
            cookie.Name.Equals(name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(cookie.Value));

    private string AuthorizeUrl(string clientId, string redirectUri, string state) =>
        Resolve("/oauth/authorize?client_id=" + Uri.EscapeDataString(clientId) +
            "&response_type=code" +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&state=" + Uri.EscapeDataString(state)).AbsoluteUri;

    internal static bool IsOAuthCallback(string location, string redirectUri)
    {
        if (location.StartsWith(redirectUri, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)) return false;
        return (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) &&
            uri.AbsolutePath is "/callback" or "/callback/";
    }

    internal static string WithOAuthState(string callbackUrl, string state)
    {
        if (BangumiOAuthConstants.QueryParameter(callbackUrl, "state") is not null) return callbackUrl;
        var separator = callbackUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return callbackUrl + separator + "state=" + Uri.EscapeDataString(state);
    }

    internal static string ImageContentType(string header)
    {
        var mime = header.Split(';')[0].Trim().ToLowerInvariant();
        return mime is "image/gif" or "image/png" or "image/jpeg" or "image/webp" ? mime : "image/gif";
    }

    internal sealed record BangumiLoginForm(string Action, string Formhash);

    internal static BangumiLoginForm? ParseLoginForm(string html)
    {
        var form = FindForm(html, (attributes, body) =>
            body.Contains("name=\"formhash\"", StringComparison.Ordinal) ||
            body.Contains("name='formhash'", StringComparison.Ordinal) ||
            (attributes.TryGetValue("id", out var id) && id.Equals("loginForm", StringComparison.OrdinalIgnoreCase)));
        if (form is null) return null;
        var hash = FindInputValue(form.Value.Body, "formhash");
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var action = form.Value.Attributes.TryGetValue("action", out var value) && value.Length > 0
            ? value
            : "/FollowTheRabbit";
        return new BangumiLoginForm(action, hash);
    }

    internal sealed record BangumiAuthorizeForm(string Action, IReadOnlyList<KeyValuePair<string, string>> Fields);

    internal static BangumiAuthorizeForm? ParseAuthorizeForm(string html)
    {
        var form = FindForm(html, (attributes, body) =>
            body.Contains("name=\"formhash\"", StringComparison.Ordinal) ||
            body.Contains("name='formhash'", StringComparison.Ordinal));
        if (form is null) return null;
        var bodyText = form.Value.Body;
        var containsAuthorizeAction = form.Value.Attributes.TryGetValue("action", out var action) &&
            action.Contains("oauth/authorize", StringComparison.OrdinalIgnoreCase);
        var hasAuthorizeButton = bodyText.Contains("授权", StringComparison.Ordinal);
        if (!containsAuthorizeAction && !hasAuthorizeButton) return null;

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var input in ParseInputs(bodyText))
        {
            var type = input.Type;
            if (type is "submit" or "button" or "image") continue;
            if (type is "checkbox" or "radio" && !input.Checked) continue;
            if (input.Name.Length == 0) continue;
            fields.Add(new KeyValuePair<string, string>(input.Name, input.Value));
        }
        if (fields.All(field => !field.Key.Equals("formhash", StringComparison.Ordinal) || field.Value.Length == 0))
            return null;
        var submit = ParseInputs(bodyText).FirstOrDefault(input =>
            input.Type is "submit" or "button" && input.Value.Contains("授权", StringComparison.Ordinal));
        var submitName = submit is not null && submit.Name.Length > 0 ? submit.Name : "submit";
        var submitValue = submit is not null && submit.Value.Length > 0 ? submit.Value : "授权";
        if (fields.All(field => !field.Key.Equals(submitName, StringComparison.Ordinal)))
            fields.Add(new KeyValuePair<string, string>(submitName, submitValue));
        // Keep a blank action blank: the caller must post back to the page URL (query included).
        var formAction = form.Value.Attributes.TryGetValue("action", out var rawAction) ? rawAction : string.Empty;
        return new BangumiAuthorizeForm(formAction, fields);
    }

    internal static string? ParseBangumiNotice(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        foreach (Match message in MessageBlock.Matches(html))
        {
            var paragraph = NoticeParagraph.Match(message.Groups[1].Value);
            var text = CleanText(paragraph.Success ? paragraph.Groups[1].Value : message.Groups[1].Value);
            if (text.Length > 0) return text;
        }
        return null;
    }

    private static string CleanText(string html)
    {
        var text = HtmlTag.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static (Dictionary<string, string> Attributes, string Body)? FindForm(
        string html,
        Func<Dictionary<string, string>, string, bool> predicate)
    {
        foreach (Match form in FormTag.Matches(html))
        {
            var close = html.IndexOf("</form>", form.Index, StringComparison.OrdinalIgnoreCase);
            if (close < 0) continue;
            var bodyStart = form.Index + form.Length;
            if (close <= bodyStart) continue;
            var body = html[bodyStart..close];
            var attributes = ParseAttributes(form.Value);
            if (predicate(attributes, body)) return (attributes, body);
        }
        return null;
    }

    private static string FindInputValue(string body, string name)
    {
        foreach (var input in ParseInputs(body))
        {
            if (input.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return input.Value;
        }
        return string.Empty;
    }

    private static List<HtmlInput> ParseInputs(string html)
    {
        var inputs = new List<HtmlInput>();
        foreach (Match match in InputTag.Matches(html))
        {
            var attributes = ParseAttributes(match.Value);
            attributes.TryGetValue("name", out var name);
            attributes.TryGetValue("value", out var value);
            attributes.TryGetValue("type", out var type);
            inputs.Add(new HtmlInput(name ?? string.Empty, value ?? string.Empty, (type ?? "text").ToLowerInvariant(), attributes.ContainsKey("checked")));
        }
        return inputs;
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributePattern.Matches(tag))
        {
            var value = attribute.Groups[3].Success
                ? attribute.Groups[3].Value
                : attribute.Groups[4].Success ? attribute.Groups[4].Value : attribute.Groups[5].Value;
            attributes[attribute.Groups[1].Value] = WebUtility.HtmlDecode(value);
        }
        return attributes;
    }

    private sealed record HtmlInput(string Name, string Value, string Type, bool Checked);

    private sealed record HttpResult(int Code, string? Location, string Body, string ContentType, byte[] Bytes);

    private sealed record Landed(string Body, string? CallbackUrl);

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
