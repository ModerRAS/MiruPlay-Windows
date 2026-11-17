namespace MiruPlay.Windows.Services;

public sealed record BangumiWebLoginDto(
    string Status,
    string? Message = null,
    string? CaptchaImage = null,
    bool TokenConfigured = false,
    long ExpiresAtMillis = 0);

/// <summary>
/// Session-oriented Bangumi account/password login for the web console. The browser only
/// talks to this device; the device fetches the captcha and posts the password to Bangumi,
/// then hands the resulting loopback callback to the shared OAuth coordinator. Passwords are
/// only forwarded into the one login request and are never persisted or echoed back.
/// Sessions expire after ten minutes and are discarded on failure/cancel/logout.
/// </summary>
public sealed class BangumiWebConsoleLogin : IDisposable
{
    public const string Idle = "idle";
    public const string Challenge = "challenge";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string NotConfigured = "not_configured";
    public const long SessionTtlMillis = 10L * 60L * 1000L;

    private readonly IBangumiOAuthFlow _oauth;
    private readonly Func<IBangumiPasswordLoginApi> _newClient;
    private readonly Func<bool> _tokenConfigured;
    private readonly Func<long> _expiresAtMillis;
    private readonly Func<long> _clockMillis;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private IBangumiPasswordLoginApi? _session;
    private long _sessionStartedAt;
    private bool _disposed;

    public BangumiWebConsoleLogin(
        IBangumiOAuthFlow oauth,
        Func<IBangumiPasswordLoginApi> newClient,
        Func<bool> tokenConfigured,
        Func<long>? expiresAtMillis = null,
        Func<long>? clockMillis = null)
    {
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _newClient = newClient ?? throw new ArgumentNullException(nameof(newClient));
        _tokenConfigured = tokenConfigured ?? (() => false);
        _expiresAtMillis = expiresAtMillis ?? (() => 0);
        _clockMillis = clockMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<BangumiWebLoginDto> StartAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var authorizeUrl = _oauth.StartAuthorize();
            if (string.IsNullOrEmpty(authorizeUrl))
            {
                ClearSession();
                return Snapshot(NotConfigured, _oauth.State.Message ?? AppStrings.Get("bgmOauth.notConfigured"));
            }
            var client = _newClient();
            var step = await client.StartChallengeAsync(cancellationToken).ConfigureAwait(false);
            switch (step)
            {
                case BangumiPasswordLoginStep.Challenge challenge:
                    _session = client;
                    _sessionStartedAt = _clockMillis();
                    return Snapshot(Challenge, captcha: challenge);
                case BangumiPasswordLoginStep.Failed failure:
                    client.Dispose();
                    _oauth.Cancel();
                    ClearSession();
                    return Snapshot(Failed, failure.Message);
                default:
                    client.Dispose();
                    _oauth.Cancel();
                    ClearSession();
                    return Snapshot(Failed, AppStrings.Get("bgmOauth.captchaFailed"));
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<BangumiWebLoginDto> RefreshCaptchaAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var client = LiveSession();
            if (client is null) return Snapshot(Failed, AppStrings.Get("bgmOauth.needChallenge"));
            var step = await client.RefreshCaptchaAsync(cancellationToken).ConfigureAwait(false);
            return step switch
            {
                BangumiPasswordLoginStep.Challenge challenge => Snapshot(Challenge, captcha: challenge),
                BangumiPasswordLoginStep.Failed failure => Snapshot(Failed, failure.Message),
                _ => Snapshot(Failed, AppStrings.Get("bgmOauth.captchaFailed")),
            };
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<BangumiWebLoginDto> SubmitAsync(
        string email,
        string password,
        string captcha,
        CancellationToken cancellationToken = default)
    {
        var trimmedEmail = email?.Trim() ?? string.Empty;
        var trimmedCaptcha = captcha?.Trim() ?? string.Empty;
        if (trimmedEmail.Length == 0 || string.IsNullOrEmpty(password) || trimmedCaptcha.Length == 0)
            throw new ArgumentException(AppStrings.Get("bgmOauth.fieldsRequired"));
        if (trimmedEmail.Length > 320 || password.Length > 256 || trimmedCaptcha.Length > 64)
            throw new ArgumentException(AppStrings.Get("bgmOauth.fieldsTooLong"));

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var client = LiveSession();
            if (client is null) return Snapshot(Failed, AppStrings.Get("bgmOauth.loginExpired"));
            if (!_oauth.IsConfigured)
            {
                _oauth.Cancel();
                ClearSession();
                return Snapshot(NotConfigured, AppStrings.Get("bgmOauth.notConfigured"));
            }
            var state = _oauth.State;
            if (state.Status != BangumiOAuthStatus.Authorizing || string.IsNullOrEmpty(state.AuthorizeUrl))
                return Snapshot(Failed, AppStrings.Get("bgmOauth.loginExpired"));
            var clientId = BangumiOAuthConstants.QueryParameter(state.AuthorizeUrl, "client_id") ?? string.Empty;
            var oauthState = BangumiOAuthConstants.QueryParameter(state.AuthorizeUrl, "state") ?? string.Empty;
            if (clientId.Length == 0 || oauthState.Length == 0)
                return Snapshot(Failed, AppStrings.Get("bgmOauth.loginExpired"));

            var step = await client.LoginAndAuthorizeAsync(
                trimmedEmail,
                password,
                trimmedCaptcha,
                clientId,
                _oauth.CallbackUrl,
                oauthState,
                cancellationToken).ConfigureAwait(false);
            switch (step)
            {
                case BangumiPasswordLoginStep.Authorized authorized:
                {
                    var accepted = await _oauth.SubmitCallbackUrlAsync(authorized.CallbackUrl, cancellationToken).ConfigureAwait(false);
                    ClearSession();
                    if (!accepted) return Snapshot(Failed, AppStrings.Get("bgmOauth.callbackRejected"));
                    return _oauth.State.Status == BangumiOAuthStatus.Success
                        ? Snapshot(Success)
                        : Snapshot(Failed, _oauth.State.Message ?? AppStrings.Get("bgmOauth.failed"));
                }
                case BangumiPasswordLoginStep.Retry retry:
                    return Snapshot(Challenge, retry.Message, captcha: new BangumiPasswordLoginStep.Challenge(retry.Image, retry.ContentType));
                case BangumiPasswordLoginStep.Challenge challenge:
                    return Snapshot(Challenge, captcha: challenge);
                case BangumiPasswordLoginStep.Failed failure:
                    _oauth.Cancel();
                    ClearSession();
                    return Snapshot(Failed, failure.Message);
                default:
                    return Snapshot(Failed, AppStrings.Get("bgmOauth.failed"));
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<BangumiWebLoginDto> CancelAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ClearSession();
            _oauth.Cancel();
            return Snapshot(Idle);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<BangumiWebLoginDto> LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ClearSession();
            _oauth.Logout();
            return Snapshot(Idle);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private IBangumiPasswordLoginApi? LiveSession()
    {
        var current = _session;
        if (current is null) return null;
        if (_clockMillis() - _sessionStartedAt > SessionTtlMillis)
        {
            ClearSession();
            _oauth.Cancel();
            return null;
        }
        return current;
    }

    private void ClearSession()
    {
        _session?.Dispose();
        _session = null;
        _sessionStartedAt = 0;
    }

    private BangumiWebLoginDto Snapshot(
        string status,
        string? message = null,
        string? captchaImage = null,
        BangumiPasswordLoginStep.Challenge? captcha = null) =>
        new(
            status,
            message,
            captcha is not null ? ToDataUrl(captcha.Image, captcha.ContentType) : captchaImage,
            _tokenConfigured(),
            _expiresAtMillis());

    private static string ToDataUrl(byte[] image, string contentType)
    {
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mime.Length == 0) mime = "image/gif";
        return $"data:{mime};base64,{Convert.ToBase64String(image)}";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearSession();
        _mutex.Dispose();
        GC.SuppressFinalize(this);
    }
}
