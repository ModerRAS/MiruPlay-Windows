using System.Windows.Input;
using System.Windows.Threading;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// Remote-control actions the TV shell understands. Devices are normalised to
/// these actions so screens never see raw key codes.
/// </summary>
public enum TvInputAction
{
    Up,
    Down,
    Left,
    Right,
    Select,
    Back,
    Home,
    Menu,
    PlayPause,
    Stop,
    SeekBack,
    SeekForward,
    PrevEpisode,
    NextEpisode,
    VolumeUp,
    VolumeDown,
    Mute,
    SpeedDown,
    SpeedUp,
    Info,
    Subtitles,
    AudioTrack,
    Exit,
}

public enum TvRemotePreset
{
    General,
    MceRc6,
    FireTv,
}

/// <summary>
/// Key -> action mapping presets. General keyboard, MCE RC6 remotes and
/// Fire TV style Bluetooth remotes all land on the same logical actions; the
/// resolver also accepts the union of all presets so an unknown remote still
/// works before the user opens the diagnostics page.
/// </summary>
public static class TvKeyMap
{
    public static IReadOnlyList<string> PresetIds { get; } = ["general", "mce", "firetv"];

    private static readonly Dictionary<Key, TvInputAction> GeneralBindings = new()
    {
        [Key.Up] = TvInputAction.Up,
        [Key.Down] = TvInputAction.Down,
        [Key.Left] = TvInputAction.Left,
        [Key.Right] = TvInputAction.Right,
        [Key.Enter] = TvInputAction.Select,
        [Key.Space] = TvInputAction.Select,
        [Key.Escape] = TvInputAction.Back,
        [Key.Back] = TvInputAction.Back,
        [Key.Home] = TvInputAction.Home,
        [Key.M] = TvInputAction.Menu,
        [Key.MediaPlayPause] = TvInputAction.PlayPause,
        [Key.P] = TvInputAction.PlayPause,
        [Key.K] = TvInputAction.PlayPause,
        [Key.MediaStop] = TvInputAction.Stop,
        [Key.MediaPreviousTrack] = TvInputAction.PrevEpisode,
        [Key.MediaNextTrack] = TvInputAction.NextEpisode,
        [Key.J] = TvInputAction.SeekBack,
        [Key.L] = TvInputAction.SeekForward,
        [Key.I] = TvInputAction.Info,
        [Key.C] = TvInputAction.Subtitles,
        [Key.B] = TvInputAction.AudioTrack,
        [Key.OemMinus] = TvInputAction.SpeedDown,
        [Key.OemPlus] = TvInputAction.SpeedUp,
        [Key.VolumeUp] = TvInputAction.VolumeUp,
        [Key.VolumeDown] = TvInputAction.VolumeDown,
        [Key.VolumeMute] = TvInputAction.Mute,
    };

    private static readonly Dictionary<Key, TvInputAction> MceBindings = new()
    {
        [Key.Up] = TvInputAction.Up,
        [Key.Down] = TvInputAction.Down,
        [Key.Left] = TvInputAction.Left,
        [Key.Right] = TvInputAction.Right,
        [Key.Enter] = TvInputAction.Select,
        [Key.BrowserBack] = TvInputAction.Back,
        [Key.Escape] = TvInputAction.Back,
        [Key.BrowserHome] = TvInputAction.Home,
        [Key.Home] = TvInputAction.Home,
        [Key.M] = TvInputAction.Menu,
        [Key.MediaPlayPause] = TvInputAction.PlayPause,
        [Key.MediaStop] = TvInputAction.Stop,
        [Key.MediaPreviousTrack] = TvInputAction.PrevEpisode,
        [Key.MediaNextTrack] = TvInputAction.NextEpisode,
        [Key.VolumeUp] = TvInputAction.VolumeUp,
        [Key.VolumeDown] = TvInputAction.VolumeDown,
        [Key.VolumeMute] = TvInputAction.Mute,
        [Key.I] = TvInputAction.Info,
    };

    private static readonly Dictionary<Key, TvInputAction> FireTvBindings = new()
    {
        [Key.Up] = TvInputAction.Up,
        [Key.Down] = TvInputAction.Down,
        [Key.Left] = TvInputAction.Left,
        [Key.Right] = TvInputAction.Right,
        [Key.Enter] = TvInputAction.Select,
        [Key.Escape] = TvInputAction.Back,
        [Key.Back] = TvInputAction.Back,
        [Key.BrowserBack] = TvInputAction.Back,
        [Key.Home] = TvInputAction.Home,
        [Key.M] = TvInputAction.Menu,
        [Key.MediaPlayPause] = TvInputAction.PlayPause,
        [Key.MediaStop] = TvInputAction.Stop,
        [Key.VolumeUp] = TvInputAction.VolumeUp,
        [Key.VolumeDown] = TvInputAction.VolumeDown,
        [Key.VolumeMute] = TvInputAction.Mute,
    };

    private static readonly Dictionary<Key, TvInputAction> UniversalBindings = Merge();

    public static string NormalizePresetId(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        return candidate is not null && PresetIds.Contains(candidate) ? candidate : "general";
    }

    public static TvRemotePreset ParsePreset(string? value) => NormalizePresetId(value) switch
    {
        "mce" => TvRemotePreset.MceRc6,
        "firetv" => TvRemotePreset.FireTv,
        _ => TvRemotePreset.General,
    };

    public static string ToId(TvRemotePreset preset) => preset switch
    {
        TvRemotePreset.MceRc6 => "mce",
        TvRemotePreset.FireTv => "firetv",
        _ => "general",
    };

    public static IReadOnlyDictionary<Key, TvInputAction> Bindings(TvRemotePreset preset) => preset switch
    {
        TvRemotePreset.MceRc6 => MceBindings,
        TvRemotePreset.FireTv => FireTvBindings,
        _ => GeneralBindings,
    };

    /// <summary>Resolve a key for the selected preset, falling back to the union of presets.</summary>
    public static TvInputAction? Resolve(TvRemotePreset preset, Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Shift)
        {
            if (key == Key.Left) return TvInputAction.SeekBack;
            if (key == Key.Right) return TvInputAction.SeekForward;
            return null;
        }
        if (modifiers != ModifierKeys.None) return null;
        if (Bindings(preset).TryGetValue(key, out var action)) return action;
        return UniversalBindings.TryGetValue(key, out var universal) ? universal : null;
    }

    public static bool IsRepeatable(TvInputAction action) => action is
        TvInputAction.Up or TvInputAction.Down or TvInputAction.Left or TvInputAction.Right or
        TvInputAction.SeekBack or TvInputAction.SeekForward or
        TvInputAction.VolumeUp or TvInputAction.VolumeDown;

    private static Dictionary<Key, TvInputAction> Merge()
    {
        var merged = new Dictionary<Key, TvInputAction>(GeneralBindings);
        foreach (var (key, action) in MceBindings) merged.TryAdd(key, action);
        foreach (var (key, action) in FireTvBindings) merged.TryAdd(key, action);
        return merged;
    }
}

public sealed class TvInputActionEventArgs(TvInputAction action) : EventArgs
{
    public TvInputAction Action { get; } = action;
}

public sealed class TvBackHoldProgressEventArgs(double progress, bool completed) : EventArgs
{
    public double Progress { get; } = progress;
    public bool Completed { get; } = completed;
}

/// <summary>
/// Normalises key input into actions, owning repeat throttling (350 ms initial
/// delay, then 90 ms per step) and the long-press Back exit gesture. Pump is
/// public so unit tests can drive time deterministically; the WPF instance runs
/// a 25 ms dispatcher timer.
/// </summary>
public sealed class TvInputService : IDisposable
{
    public const int RepeatingInitialDelayMs = 350;
    public const int RepeatingIntervalMs = 90;
    public const int DefaultBackHoldMs = 2000;

    private readonly Func<long> _clock;
    private readonly DispatcherTimer? _timer;
    private readonly HashSet<Key> _heldKeys = [];
    private readonly int _backHoldMs;
    private TvInputAction? _repeatAction;
    private Key? _repeatKey;
    private long _nextRepeatAt;
    private bool _backHeld;
    private bool _backCompleted;
    private long _backDownAt;
    private double _lastReportedProgress = -1;

    public TvInputService(
        TvRemotePreset preset = TvRemotePreset.General,
        int backHoldMs = DefaultBackHoldMs,
        Func<long>? clock = null,
        bool startTimer = true)
    {
        Preset = preset;
        _backHoldMs = Math.Clamp(backHoldMs, 500, 10_000);
        _clock = clock ?? (() => Environment.TickCount64);
        if (!startTimer) return;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Pump(_clock());
        _timer.Start();
    }

    public TvRemotePreset Preset { get; }

    public int BackHoldMs => _backHoldMs;

    public bool IsBackHeld => _backHeld;

    public double BackHoldProgress { get; private set; }

    /// <summary>Raised when a key resolves to an action (including repeats and single Back taps).</summary>
    public event EventHandler<TvInputActionEventArgs>? ActionInvoked;

    public event EventHandler? BackHoldStarted;

    public event EventHandler<TvBackHoldProgressEventArgs>? BackHoldProgressChanged;

    /// <summary>Raised once the Back key has been held long enough to exit TV mode.</summary>
    public event EventHandler? BackHoldCompleted;

    /// <summary>Raised when Back is released early, before the long-press threshold.</summary>
    public event EventHandler? BackHoldCancelled;

    public bool HandleKeyDown(Key key, ModifierKeys modifiers)
    {
        var action = TvKeyMap.Resolve(Preset, key, modifiers);
        if (action is null) return false;
        if (!_heldKeys.Add(key)) return true; // OS auto-repeat: we drive our own cadence.

        var now = _clock();
        if (action == TvInputAction.Back)
        {
            if (_backHeld) return true;
            _backHeld = true;
            _backCompleted = false;
            _backDownAt = now;
            _lastReportedProgress = -1;
            BackHoldProgress = 0;
            BackHoldStarted?.Invoke(this, EventArgs.Empty);
            BackHoldProgressChanged?.Invoke(this, new TvBackHoldProgressEventArgs(0, false));
            return true;
        }

        Raise(action.Value);
        if (TvKeyMap.IsRepeatable(action.Value))
        {
            _repeatKey = key;
            _repeatAction = action;
            _nextRepeatAt = now + RepeatingInitialDelayMs;
        }
        return true;
    }

    public void HandleKeyUp(Key key)
    {
        if (!_heldKeys.Remove(key)) return;
        if (_repeatKey == key)
        {
            _repeatKey = null;
            _repeatAction = null;
        }
        if (key is not (Key.Escape or Key.Back or Key.BrowserBack) || !_backHeld) return;
        _backHeld = false;
        if (_backCompleted)
        {
            _backCompleted = false;
            return;
        }
        BackHoldProgress = 0;
        BackHoldCancelled?.Invoke(this, EventArgs.Empty);
        Raise(TvInputAction.Back);
    }

    /// <summary>Cancel an in-flight Back long press without dispatching Back.</summary>
    public void CancelBackHold()
    {
        _backHeld = false;
        _backCompleted = false;
        BackHoldProgress = 0;
    }

    /// <summary>Advance timers; safe to call from tests with a fake clock.</summary>
    public void Pump(long nowMs)
    {
        if (_backHeld)
        {
            var progress = Math.Clamp((nowMs - _backDownAt) / (double)_backHoldMs, 0, 1);
            BackHoldProgress = progress;
            if (Math.Abs(progress - _lastReportedProgress) >= 0.02 || progress >= 1)
            {
                _lastReportedProgress = progress;
                BackHoldProgressChanged?.Invoke(this, new TvBackHoldProgressEventArgs(progress, false));
            }
            if (progress >= 1 && !_backCompleted)
            {
                _backCompleted = true;
                BackHoldProgressChanged?.Invoke(this, new TvBackHoldProgressEventArgs(1, true));
                BackHoldCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        if (_repeatAction is { } repeat && _repeatKey is { } repeatKey && nowMs >= _nextRepeatAt)
        {
            Raise(repeat);
            _nextRepeatAt += RepeatingIntervalMs;
            if (_nextRepeatAt < nowMs) _nextRepeatAt = nowMs + RepeatingIntervalMs;
            _ = repeatKey;
        }
    }

    public void Reset()
    {
        _heldKeys.Clear();
        _repeatKey = null;
        _repeatAction = null;
        CancelBackHold();
    }

    public void Dispose()
    {
        _timer?.Stop();
        Reset();
    }

    private void Raise(TvInputAction action) => ActionInvoked?.Invoke(this, new TvInputActionEventArgs(action));
}