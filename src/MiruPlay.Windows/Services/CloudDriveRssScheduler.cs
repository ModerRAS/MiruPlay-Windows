namespace MiruPlay.Windows.Services;

public sealed class CloudDriveRssScheduler : IDisposable, IAsyncDisposable
{
    private readonly Func<CloudDriveAutomationConfig> _loadConfig;
    private readonly Func<CancellationToken, Task> _run;
    private readonly Func<bool> _isRunning;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private long _lastAttemptAt;
    private bool _disposed;

    public CloudDriveRssScheduler(CloudDriveAutomationStore config, CloudDriveRssRunner runner)
        : this(
            config.Load,
            async cancellationToken => { await runner.RunAsync(cancellationToken).ConfigureAwait(false); },
            () => runner.Status.Running,
            TimeProvider.System,
            TimeSpan.FromMinutes(1))
    {
    }

    internal CloudDriveRssScheduler(
        Func<CloudDriveAutomationConfig> loadConfig,
        Func<CancellationToken, Task> run,
        Func<bool> isRunning,
        TimeProvider timeProvider,
        TimeSpan pollInterval)
    {
        _loadConfig = loadConfig;
        _run = run;
        _isRunning = isRunning;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= RunLoopAsync(_cancellation.Token);
    }

    internal async Task<bool> RunIfDueAsync(long now, CancellationToken cancellationToken = default)
    {
        var config = _loadConfig();
        if (!config.Enabled || _isRunning()) return false;
        var intervalMs = checked((long)TimeSpan.FromMinutes(config.IntervalMinutes).TotalMilliseconds);
        var basis = Math.Max(config.LastRunAt, Volatile.Read(ref _lastAttemptAt));
        if (basis > 0 && now - basis < intervalMs) return false;
        Interlocked.Exchange(ref _lastAttemptAt, now);
        try
        {
            await _run(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidDataException or InvalidOperationException)
        {
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cancellation.CancelAsync().ConfigureAwait(false);
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
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException)
            {
            }
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
