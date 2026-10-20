namespace MiruPlay.Windows.Services;

public sealed class OpenObserveLogScheduler : IAsyncDisposable
{
    private static readonly TimeSpan UploadInterval = TimeSpan.FromHours(1);
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<Func<AppSettings, AppSettings>, AppSettings> _updateSettings;
    private readonly OpenObserveLogService _logs;
    private readonly HeadlessTaskScheduler _tasks;
    private readonly RotatingLocalLogStore? _localLogs;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private long _lastAttemptAt;

    public OpenObserveLogScheduler(
        Func<AppSettings> getSettings,
        Func<Func<AppSettings, AppSettings>, AppSettings> updateSettings,
        OpenObserveLogService logs,
        HeadlessTaskScheduler tasks,
        TimeProvider? timeProvider = null,
        RotatingLocalLogStore? localLogs = null)
    {
        _getSettings = getSettings;
        _updateSettings = updateSettings;
        _logs = logs;
        _tasks = tasks;
        _localLogs = localLogs;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Start() => _loop ??= RunAsync(_shutdown.Token);

    public async Task<bool> RunIfDueAsync(long nowUnixMilliseconds, CancellationToken cancellationToken = default)
    {
        var settings = _getSettings();
        if (!settings.LogUploadEnabled) return false;
        var dueBasis = Math.Max(settings.LastLogUploadAt, Interlocked.Read(ref _lastAttemptAt));
        if (dueBasis > 0 && nowUnixMilliseconds - dueBasis < UploadInterval.TotalMilliseconds) return false;
        Interlocked.Exchange(ref _lastAttemptAt, nowUnixMilliseconds);
        return await _tasks.RunTrackedAsync("openobserve-log-upload", "OpenObserve 日志上报", async token =>
        {
            var result = await _logs.UploadAsync(settings, token).ConfigureAwait(false);
            _updateSettings(current => current with
            {
                LastLogUploadAt = result.CompletedAt,
                LastLogUploadStatus = result.Message,
            });
            _localLogs?.Write(result.Succeeded ? "INFO" : "ERROR", "OpenObserve scheduled upload completed",
                attributes: new Dictionary<string, string>
                {
                    ["uploadedCount"] = result.UploadedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["succeeded"] = result.Succeeded.ToString(),
                });
            if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _timeProvider);
        do
        {
            try { await RunIfDueAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
            {
                _localLogs?.Write("ERROR", "OpenObserve automatic upload failed", error);
                _updateSettings(current => current with { LastLogUploadStatus = RotatingLocalLogStore.Redact(error.Message) });
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
