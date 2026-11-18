using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Services;

public enum AutoUpdateStateKind
{
    Idle,
    Checking,
    Downloading,
    Available,
    Ready,
    Failed,
}

public sealed record AutoUpdateState(
    AutoUpdateStateKind Kind,
    WindowsUpdateStatus? Status = null,
    string? Error = null,
    long ChangedAt = 0,
    string? VersionName = null);

/// <summary>
/// Background auto-update loop: one check shortly after startup, then every
/// N hours. Never installs while running; install modes only prepare the staged
/// installer and let the exit flow or settings page act.
/// </summary>
public sealed class AutoUpdateScheduler : IDisposable
{
    public const string ModeOff = "off";
    public const string ModeCheck = "check";
    public const string ModeDownload = "download";
    public const string ModeInstall = "install";
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly string[] ValidModes = [ModeOff, ModeCheck, ModeDownload, ModeInstall];

    private readonly Func<AppSettings> _getSettings;
    private readonly IUpdateChecker _updater;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _startupDelay;
    private readonly Action<AutoUpdateState>? _onState;
    private readonly object _sync = new();
    private Timer? _timer;
    private long _startedAt;
    private long _nextDueAt;
    private bool _busy;
    private bool _disposed;
    private AutoUpdateState _state = new(AutoUpdateStateKind.Idle);

    public AutoUpdateScheduler(
        Func<AppSettings> getSettings,
        IUpdateChecker updater,
        Action<AutoUpdateState>? onState = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? startupDelay = null)
    {
        _getSettings = getSettings;
        _updater = updater;
        _onState = onState;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _startupDelay = startupDelay ?? StartupDelay;
    }

    public AutoUpdateState State => _state;

    public static string NormalizeMode(string? mode) =>
        ValidModes.FirstOrDefault(value => value.Equals(mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ModeDownload;

    public static bool IsEnabled(string? mode) => !string.Equals(NormalizeMode(mode), ModeOff, StringComparison.Ordinal);

    public static int ModeRank(string? mode) => NormalizeMode(mode) switch
    {
        ModeCheck => 1,
        ModeDownload => 2,
        ModeInstall => 3,
        _ => 0,
    };

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _timer is not null) return;
            if (!IsEnabled(_getSettings().AutoUpdateMode) || !_updater.IsSupported) return;
            _startedAt = _clock().ToUnixTimeMilliseconds();
            _nextDueAt = _startedAt + (long)_startupDelay.TotalMilliseconds;
            _timer = new Timer(_ => _ = TickAsync(), null, PollInterval, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Force a check from the UI; does not require the timer to be running.</summary>
    public Task TickAsync(bool force = false) => RunAsync(force, CancellationToken.None);

    public async Task<AutoUpdateState> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(force: true, cancellationToken).ConfigureAwait(false);
        return _state;
    }

    private async Task RunAsync(bool force, CancellationToken cancellationToken)
    {
        var settings = _getSettings();
        var mode = NormalizeMode(settings.AutoUpdateMode);
        if ((!IsEnabled(mode) && !force) || !_updater.IsSupported || _disposed) return;
        var now = _clock().ToUnixTimeMilliseconds();
        if (!force)
        {
            lock (_sync)
            {
                if (_nextDueAt == 0) _nextDueAt = now + (long)_startupDelay.TotalMilliseconds;
                if (now < _nextDueAt) return;
                _nextDueAt = now + Math.Clamp(settings.AutoUpdateIntervalHours, 1, 168) * 60L * 60L * 1000L;
            }
        }
        if (_busy) return;
        _busy = true;
        WriteLog($"tick mode={mode} force={force}");
        SetState(new AutoUpdateState(AutoUpdateStateKind.Checking, _updater.Status, ChangedAt: now));
        try
        {
            var status = await _updater.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (status.UpdateAvailable && ModeRank(mode) >= ModeRank(ModeDownload) &&
                status.StagedInstallerPath is null)
            {
                SetState(new AutoUpdateState(AutoUpdateStateKind.Downloading, status, ChangedAt: _clock().ToUnixTimeMilliseconds(), VersionName: status.Latest?.VersionName));
                status = await _updater.DownloadAsync(cancellationToken).ConfigureAwait(false);
            }
            var kind = status.StagedInstallerPath is not null
                ? AutoUpdateStateKind.Ready
                : status.UpdateAvailable ? AutoUpdateStateKind.Available : AutoUpdateStateKind.Idle;
            SetState(new AutoUpdateState(kind, status, ChangedAt: _clock().ToUnixTimeMilliseconds(), VersionName: status.Latest?.VersionName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            SetState(new AutoUpdateState(AutoUpdateStateKind.Failed, _updater.Status, RotatingLocalLogStore.Redact(error.Message), _clock().ToUnixTimeMilliseconds()));
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetState(AutoUpdateState state)
    {
        _state = state;
        WriteLog($"state={state.Kind} version={state.VersionName ?? "-"} error={state.Error ?? "-"}");
        try
        {
            _onState?.Invoke(state);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // Listener failures must never break the update loop.
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "auto-update.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging must never break the update loop.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}