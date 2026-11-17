using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public static class BangumiOAuthConstants
{
    public const string AuthorizeUrl = "https://bgm.tv/oauth/authorize";
    public const string TokenUrl = "https://bgm.tv/oauth/access_token";
    public const string UserInfoUrl = "https://api.bgm.tv/v0/me";
    public const string UserAgent = "ModerRAS/MiruPlay-Windows/0.1.0 (Windows) (https://github.com/ModerRAS/MiruPlay-Windows)";
    public const int DefaultCallbackPort = 13712;
    public const string CallbackPath = "/callback";

    public static string CallbackUrl(int port) =>
        $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{CallbackPath}";

    /// <summary>Bangumi OAuth2 authorize URL; client_id is public protocol data, the
    /// client secret never appears here (it is only posted in the token body).</summary>
    public static string BuildAuthorizeUrl(string clientId, string state, string callbackUrl) =>
        $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId)}" +
        "&response_type=code" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&redirect_uri={Uri.EscapeDataString(callbackUrl)}";

    /// <summary>Lenient query-string parameter reader for callback URLs.</summary>
    public static string? QueryParameter(string? url, string name)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0) return null;
        var fragmentStart = url.IndexOf('#', queryStart);
        var query = fragmentStart < 0 ? url[(queryStart + 1)..] : url[(queryStart + 1)..fragmentStart];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (!key.Equals(name, StringComparison.Ordinal)) continue;
            return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return null;
    }
}

public sealed record BangumiOAuthTokens(string AccessToken, string RefreshToken, long ExpiresAtMillis, long UserId);

public sealed record BangumiOAuthUser(long Id, string Username, string? Nickname);

/// <summary>
/// Direct Bangumi OAuth2 client (no third-party relay). The redirect URI is a loopback
/// HTTP callback; the code is exchanged for a seven-day access token plus refresh token.
/// All HTTP goes through the caller-supplied handler so the unified outbound proxy applies.
/// </summary>
public sealed class BangumiOAuthClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Uri _tokenUri;
    private readonly Uri _userInfoUri;
    private readonly Func<long> _clockMillis;

    public BangumiOAuthClient(
        string clientId,
        string clientSecret,
        HttpMessageHandler? handler = null,
        Uri? tokenUri = null,
        Uri? userInfoUri = null,
        Func<long>? clockMillis = null)
    {
        _clientId = clientId?.Trim() ?? string.Empty;
        _clientSecret = clientSecret?.Trim() ?? string.Empty;
        _clockMillis = clockMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _httpClient = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _tokenUri = tokenUri ?? new Uri(BangumiOAuthConstants.TokenUrl);
        _userInfoUri = userInfoUri ?? new Uri(BangumiOAuthConstants.UserInfoUrl);
    }

    public bool IsConfigured => _clientId.Length > 0 && _clientSecret.Length > 0;

    public Task<BangumiOAuthTokens> ExchangeCodeAsync(string code, string callbackUrl, CancellationToken cancellationToken = default) =>
        RequestTokenAsync("authorization_code", callbackUrl, [new KeyValuePair<string, string>("code", code)], cancellationToken);

    public Task<BangumiOAuthTokens> RefreshAsync(string refreshToken, string callbackUrl, CancellationToken cancellationToken = default) =>
        RequestTokenAsync("refresh_token", callbackUrl, [new KeyValuePair<string, string>("refresh_token", refreshToken)], cancellationToken);

    public async Task<BangumiOAuthUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var token = accessToken.Trim();
        if (token.Length == 0) throw new InvalidOperationException(AppStrings.Get("bgm.noToken"));
        using var request = new HttpRequestMessage(HttpMethod.Get, _userInfoUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Decorate(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(AppStrings.Get("bgm.authFailed", (int)response.StatusCode), null, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        return new BangumiOAuthUser(
            Long(root, "id"),
            String(root, "username") ?? string.Empty,
            String(root, "nickname"));
    }

    private async Task<BangumiOAuthTokens> RequestTokenAsync(
        string grantType,
        string callbackUrl,
        IReadOnlyList<KeyValuePair<string, string>> extra,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException(AppStrings.Get("bgmOauth.notConfigured"));
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", grantType),
            new("client_id", _clientId),
            new("client_secret", _clientSecret),
            new("redirect_uri", callbackUrl),
        };
        fields.AddRange(extra);
        using var form = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenUri) { Content = form };
        Decorate(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(AppStrings.Get("bgmOauth.tokenHttp", (int)response.StatusCode), null, response.StatusCode);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var access = String(root, "access_token");
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidDataException(AppStrings.Get("bgmOauth.tokenMissing"));
        var expiresIn = Long(root, "expires_in");
        return new BangumiOAuthTokens(
            access,
            String(root, "refresh_token") ?? string.Empty,
            _clockMillis() + (expiresIn > 0 ? expiresIn * 1000L : 0L),
            Long(root, "user_id"));
    }

    private static void Decorate(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BangumiOAuthConstants.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
