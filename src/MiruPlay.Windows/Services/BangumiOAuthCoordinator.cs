using System.Text.Json;

namespace MiruPlay.Windows.Services;

public enum BangumiOAuthStatus
{
    Idle,
    NotConfigured,
    Authorizing,
    Exchanging,
    Success,
    Failed,
}

public sealed record BangumiOAuthState(BangumiOAuthStatus Status, string? AuthorizeUrl = null, string? Message = null)
{
    public string StatusValue => Status switch
    {
        BangumiOAuthStatus.NotConfigured => "not_configured",
        BangumiOAuthStatus.Authorizing => "authorizing",
        BangumiOAuthStatus.Exchanging => "exchanging",
        BangumiOAuthStatus.Success => "success",
        BangumiOAuthStatus.Failed => "failed",
        _ => "idle",
    };
}

public sealed record BangumiOAuthResult(bool Succeeded, BangumiOAuthState State, string? Error = null);

/// <summary>App-side contract shared by the WPF dialog, WebControl routes and the web-console
/// password flow (which feeds a callback URL without opening a browser).</summary>
public interface IBangumiOAuthFlow
{
    bool IsConfigured { get; }
    BangumiOAuthState State { get; }
    long ExpiresAtMillis { get; }
    string CallbackUrl { get; }
    string? StartAuthorize();
    Task<BangumiOAuthResult> WaitForResultAsync(CancellationToken cancellationToken = default);
    Task<bool> SubmitCallbackUrlAsync(string callbackUrl, CancellationToken cancellationToken = default);
    void Cancel();
    void Logout();
    Task<string?> EnsureValidTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives the direct Bangumi OAuth flow: authorize URL -> loopback callback -> token exchange,
/// persists the access/refresh pair through DPAPI, and rotates the token when it is within
/// 24 hours of expiry. Only one authorization flow can be pending at a time.
/// </summary>
public sealed class BangumiOAuthCoordinator : IBangumiOAuthFlow, IDisposable
{
    public const long RefreshMarginMillis = 24L * 60L * 60L * 1000L;
    public static readonly TimeSpan DefaultAuthorizationTimeout = TimeSpan.FromMinutes(10);

    private readonly MetadataTokenStore _tokens;
    private readonly Func<BangumiOAuthCredentials?> _loadCredentials;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly Func<long> _clockMillis;
    private readonly int _callbackPort;
    private readonly TimeSpan _authorizationTimeout;
    private readonly Func<int, string, IBangumiOAuthLoopback> _loopbackFactory;
    private readonly Func<string> _newState;
    private readonly object _sync = new();
    private BangumiOAuthState _state = new(BangumiOAuthStatus.Idle);
    private string? _pendingState;
    private string? _authorizeUrl;
    private IBangumiOAuthLoopback? _loopback;
    private CancellationTokenSource? _flowCancellation;

    public BangumiOAuthCoordinator(
        MetadataTokenStore tokens,
        Func<BangumiOAuthCredentials?> loadCredentials,
        Func<HttpMessageHandler>? handlerFactory = null,
        Func<long>? clockMillis = null,
        int callbackPort = BangumiOAuthConstants.DefaultCallbackPort,
        TimeSpan? authorizationTimeout = null,
        Func<int, string, IBangumiOAuthLoopback>? loopbackFactory = null,
        Func<string>? newState = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _loadCredentials = loadCredentials ?? throw new ArgumentNullException(nameof(loadCredentials));
        _handlerFactory = handlerFactory;
        _clockMillis = clockMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _callbackPort = callbackPort;
        _authorizationTimeout = authorizationTimeout ?? DefaultAuthorizationTimeout;
        _loopbackFactory = loopbackFactory ?? ((port, path) => new BangumiOAuthLoopbackServer(port, path));
        _newState = newState ?? (() => Guid.NewGuid().ToString("N"));
    }

    public BangumiOAuthState State
    {
        get
        {
            lock (_sync) return _state;
        }
    }

    public bool IsConfigured => _loadCredentials() is not null;

    public long ExpiresAtMillis => _tokens.Load().BangumiExpiresAtMillis;

    public string CallbackUrl => BangumiOAuthConstants.CallbackUrl(_callbackPort);

    /// <summary>Starts the flow and returns the browser URL. Returns null and reports
    /// NotConfigured when no OAuth app credentials are available. A second concurrent call
    /// while a flow is authorizing returns the same URL instead of opening another flow.</summary>
    public string? StartAuthorize()
    {
        IBangumiOAuthLoopback? loopback = null;
        string url;
        lock (_sync)
        {
            if (_state.Status == BangumiOAuthStatus.Authorizing && _authorizeUrl is not null) return _authorizeUrl;
            var credentials = _loadCredentials();
            if (credentials is null)
            {
                _state = new BangumiOAuthState(BangumiOAuthStatus.NotConfigured, Message: AppStrings.Get("bgmOauth.notConfigured"));
                return null;
            }
            StopLoopbackLocked();
            var state = _newState();
            _pendingState = state;
            url = BangumiOAuthConstants.BuildAuthorizeUrl(credentials.ClientId, state, BangumiOAuthConstants.CallbackUrl(_callbackPort));
            _authorizeUrl = url;
            _state = new BangumiOAuthState(BangumiOAuthStatus.Authorizing, url);
            loopback = _loopbackFactory(_callbackPort, BangumiOAuthConstants.CallbackPath);
            _loopback = loopback;
            _flowCancellation = new CancellationTokenSource();
        }
        try
        {
            loopback.Start();
        }
        catch (InvalidOperationException error)
        {
            lock (_sync)
            {
                StopLoopbackLocked();
                _pendingState = null;
                _state = new BangumiOAuthState(BangumiOAuthStatus.Failed, _authorizeUrl, error.Message);
            }
            throw;
        }
        return url;
    }

    public async Task<BangumiOAuthResult> WaitForResultAsync(CancellationToken cancellationToken = default)
    {
        IBangumiOAuthLoopback? loopback;
        CancellationTokenSource? flowCancellation;
        lock (_sync)
        {
            loopback = _loopback;
            flowCancellation = _flowCancellation;
        }
        if (loopback is null || flowCancellation is null)
            return new BangumiOAuthResult(false, State, State.Message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, flowCancellation.Token);
        try
        {
            var callbackUrl = await loopback.WaitForCallbackAsync(linked.Token)
                .WaitAsync(_authorizationTimeout, linked.Token)
                .ConfigureAwait(false);
            await SubmitCallbackUrlAsync(callbackUrl, linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                StopLoopbackLocked();
                _pendingState = null;
                _state = new BangumiOAuthState(BangumiOAuthStatus.Failed, _authorizeUrl, AppStrings.Get("bgmOauth.timeout"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Flow cancelled by the user/logout or stopped after success; state already updated.
        }
        catch (OperationCanceledException)
        {
            Cancel();
        }
        var current = State;
        return new BangumiOAuthResult(current.Status == BangumiOAuthStatus.Success, current, current.Message);
    }

    /// <summary>Accepts a callback URL (browser redirect or web-console password flow).
    /// The state is one-time: wrong or already-used states are rejected.</summary>
    public async Task<bool> SubmitCallbackUrlAsync(string callbackUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackUrl);
        BangumiOAuthCredentials? credentials;
        string code;
        lock (_sync)
        {
            var expected = _pendingState;
            if (expected is null) return false;
            if (!string.Equals(BangumiOAuthConstants.QueryParameter(callbackUrl, "state"), expected, StringComparison.Ordinal))
                return false;
            _pendingState = null;
            code = BangumiOAuthConstants.QueryParameter(callbackUrl, "code") ?? string.Empty;
            if (code.Length == 0)
            {
                StopLoopbackLocked();
                _state = new BangumiOAuthState(BangumiOAuthStatus.Failed, _authorizeUrl, AppStrings.Get("bgmOauth.missingCode"));
                return true;
            }
            _state = new BangumiOAuthState(BangumiOAuthStatus.Exchanging, _authorizeUrl);
            credentials = _loadCredentials();
        }
        if (credentials is null)
        {
            lock (_sync)
            {
                StopLoopbackLocked();
                _state = new BangumiOAuthState(BangumiOAuthStatus.Failed, _authorizeUrl, AppStrings.Get("bgmOauth.notConfigured"));
            }
            return true;
        }
        try
        {
            using var client = CreateClient(credentials);
            var tokens = await client.ExchangeCodeAsync(code, BangumiOAuthConstants.CallbackUrl(_callbackPort), cancellationToken)
                .ConfigureAwait(false);
            _tokens.SaveBangumiSession(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtMillis);
            lock (_sync)
            {
                StopLoopbackLocked();
                _state = new BangumiOAuthState(BangumiOAuthStatus.Success, _authorizeUrl);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or InvalidOperationException or IOException or JsonException or UriFormatException)
        {
            lock (_sync)
            {
                StopLoopbackLocked();
                _state = new BangumiOAuthState(BangumiOAuthStatus.Failed, _authorizeUrl, AppStrings.Get("bgmOauth.failed"));
            }
        }
        return true;
    }

    public void Cancel()
    {
        lock (_sync)
        {
            StopLoopbackLocked();
            _pendingState = null;
            _authorizeUrl = null;
            _state = new BangumiOAuthState(BangumiOAuthStatus.Idle);
        }
    }

    public void Logout()
    {
        Cancel();
        _tokens.ClearBangumi();
    }

    /// <summary>Returns a usable access token, proactively rotating the refresh token when
    /// the stored token expires within <see cref="RefreshMarginMillis"/> (24 hours).</summary>
    public async Task<string?> EnsureValidTokenAsync(CancellationToken cancellationToken = default)
    {
        var tokens = _tokens.Load();
        var access = tokens.Bangumi?.Trim();
        if (string.IsNullOrEmpty(access)) return null;
        var now = _clockMillis();
        if (tokens.BangumiExpiresAtMillis <= 0 || tokens.BangumiExpiresAtMillis - now > RefreshMarginMillis)
            return access;
        var refresh = tokens.BangumiRefreshToken?.Trim();
        var credentials = _loadCredentials();
        if (credentials is null || string.IsNullOrEmpty(refresh))
            return tokens.BangumiExpiresAtMillis > now ? access : null;
        try
        {
            using var client = CreateClient(credentials);
            var rotated = await client.RefreshAsync(refresh, BangumiOAuthConstants.CallbackUrl(_callbackPort), cancellationToken)
                .ConfigureAwait(false);
            _tokens.SaveBangumiSession(rotated.AccessToken, rotated.RefreshToken, rotated.ExpiresAtMillis);
            return rotated.AccessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or InvalidOperationException or IOException or JsonException or UriFormatException)
        {
            return tokens.BangumiExpiresAtMillis > now ? access : null;
        }
    }

    private BangumiOAuthClient CreateClient(BangumiOAuthCredentials credentials) =>
        new(credentials.ClientId, credentials.ClientSecret, _handlerFactory?.Invoke(), clockMillis: _clockMillis);

    private void StopLoopbackLocked()
    {
        try
        {
            _flowCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _flowCancellation?.Dispose();
        _flowCancellation = null;
        try
        {
            _loopback?.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _loopback?.Dispose();
        _loopback = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopLoopbackLocked();
        }
        GC.SuppressFinalize(this);
    }
}
