using System.Text.Json;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Periodic token-rotation driver. Calls <see cref="IBangumiOAuthFlow.EnsureValidTokenAsync"/>
/// (which only refreshes inside the 24 hour margin), stops cleanly on dispose, and never
/// touches tokens when the user is logged out or OAuth is unconfigured.
/// </summary>
public sealed class BangumiOAuthRefreshScheduler : IAsyncDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly IBangumiOAuthFlow _flow;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private long _lastAttemptAt;
    private bool _disposed;

    public BangumiOAuthRefreshScheduler(
        IBangumiOAuthFlow flow,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null)
    {
        _flow = flow ?? throw new ArgumentNullException(nameof(flow));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? TimeSpan.FromMinutes(1);
    }

    public void Start() => _loop ??= RunAsync(_shutdown.Token);

    internal async Task<bool> RunIfDueAsync(long now, CancellationToken cancellationToken = default)
    {
        if (!_flow.IsConfigured) return false;
        try
        {
            if (_flow.ExpiresAtMillis <= 0) return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        var basis = Volatile.Read(ref _lastAttemptAt);
        if (basis > 0 && now - basis < CheckInterval.TotalMilliseconds) return false;
        Interlocked.Exchange(ref _lastAttemptAt, now);
        try
        {
            await _flow.EnsureValidTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or InvalidOperationException or IOException or JsonException)
        {
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval, _timeProvider);
        do
        {
            try
            {
                await RunIfDueAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
