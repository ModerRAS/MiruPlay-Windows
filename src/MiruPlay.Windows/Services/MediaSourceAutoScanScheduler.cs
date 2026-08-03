namespace MiruPlay.Windows.Services;

public sealed class MediaSourceAutoScanScheduler : IDisposable, IAsyncDisposable
{
    public static readonly IReadOnlyList<int> IntervalOptionsHours = [1, 6, 12, 24];

    private readonly Func<AppSettings> _loadSettings;
    private readonly Func<IReadOnlyList<MediaSourceInfoDto>> _listSources;
    private readonly Func<long, CancellationToken, Task<SourceScanResponse>> _scan;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<long, long> _lastAttemptBySource = [];
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private bool _disposed;

    public MediaSourceAutoScanScheduler(
        Func<AppSettings> loadSettings,
        Func<IReadOnlyList<MediaSourceInfoDto>> listSources,
        Func<long, CancellationToken, Task<SourceScanResponse>> scan)
        : this(loadSettings, listSources, scan, TimeProvider.System, TimeSpan.FromMinutes(1))
    {
    }

    internal MediaSourceAutoScanScheduler(
        Func<AppSettings> loadSettings,
        Func<IReadOnlyList<MediaSourceInfoDto>> listSources,
        Func<long, CancellationToken, Task<SourceScanResponse>> scan,
        TimeProvider timeProvider,
        TimeSpan pollInterval)
    {
        _loadSettings = loadSettings;
        _listSources = listSources;
        _scan = scan;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= RunLoopAsync(_cancellation.Token);
    }

    internal async Task<int> RunIfDueAsync(long now, CancellationToken cancellationToken = default)
    {
        var settings = _loadSettings();
        if (!settings.AutoScanEnabled || !await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return 0;
        try
        {
            var intervalHours = NormalizeIntervalHours(settings.AutoScanIntervalHours);
            var intervalMs = checked((long)TimeSpan.FromHours(intervalHours).TotalMilliseconds);
            var attempted = 0;
            foreach (var source in _listSources())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lastAttempt = _lastAttemptBySource.GetValueOrDefault(source.Id);
                var basis = Math.Max(source.LastScanned, lastAttempt);
                if (basis > 0 && now - basis < intervalMs) continue;
                _lastAttemptBySource[source.Id] = now;
                attempted++;
                try
                {
                    await _scan(source.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                }
            }
            return attempted;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public static int NormalizeIntervalHours(int value) =>
        IntervalOptionsHours.Contains(value) ? value : 6;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
        _runLock.Dispose();
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
        _runLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunIfDueAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
