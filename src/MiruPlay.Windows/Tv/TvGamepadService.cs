namespace MiruPlay.Windows.Tv;

public sealed class TvGamepadActionEventArgs(TvInputAction action, string label) : EventArgs
{
    public TvInputAction Action { get; } = action;

    /// <summary>Short label used by the demo badge, e.g. "A", "←", "LT".</summary>
    public string Label { get; } = label;
}

/// <summary>
/// Turns XInput snapshots into the same <see cref="TvInputAction"/> stream the
/// keyboard/remote path uses: edge-triggered buttons, D-pad/left-stick
/// navigation with the 350ms/90ms repeat cadence, trigger-based seeking and the
/// Back-hold-to-exit gesture. Hot-plug safe and fully injectable for tests.
/// </summary>
public sealed class TvGamepadService : IDisposable
{
    public const int RepeatInitialDelayMs = TvInputService.RepeatingInitialDelayMs;
    public const int RepeatIntervalMs = TvInputService.RepeatingIntervalMs;
    public const int SeekRepeatInitialDelayMs = 350;
    public const int SeekRepeatIntervalMs = 200;
    public const byte DefaultTriggerThreshold = 153; // 0.6 * 255

    private readonly Func<long> _clock;
    private readonly int _exitHoldMs;
    private double _deadzone = 0.5;
    private TvGamepadButtons _previousButtons;
    private TvGamepadState _previousState;
    private bool _wasConnected;
    private TvInputAction? _directionAction;
    private long _nextDirectionRepeatAt;
    private string _directionLabel = string.Empty;
    private bool _leftTriggerHeld;
    private bool _rightTriggerHeld;
    private long _nextLeftSeekAt;
    private long _nextRightSeekAt;
    private bool _rightStickHeld;
    private long _nextRightStickSeekAt;
    private bool _backHeld;
    private bool _backCompleted;
    private long _backDownAt;
    private double _lastReportedProgress = -1;

    public TvGamepadService(int exitHoldMs = TvInputService.DefaultBackHoldMs, Func<long>? clock = null)
    {
        _exitHoldMs = Math.Clamp(exitHoldMs, 500, 10_000);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Normalised left-stick deadzone, clamped to the supported 0.2–0.8 range.</summary>
    public double Deadzone
    {
        get => _deadzone;
        set => _deadzone = Math.Clamp(value, 0.2, 0.8);
    }

    public byte TriggerThreshold { get; set; } = DefaultTriggerThreshold;

    public bool IsBackHeld => _backHeld;

    public double BackHoldProgress { get; private set; }

    public event EventHandler<TvGamepadActionEventArgs>? ActionInvoked;

    public event EventHandler? BackHoldStarted;

    public event EventHandler<TvBackHoldProgressEventArgs>? BackHoldProgressChanged;

    public event EventHandler? BackHoldCompleted;

    public event EventHandler? BackHoldCancelled;

    /// <summary>Feed one poll result; safe to call at ~60Hz while TV mode is active.</summary>
    public void Poll(TvGamepadState state) => Poll(state, _clock());

    internal void Poll(TvGamepadState state, long nowMs)
    {
        if (!Enabled)
        {
            ResetInternal(cancelBackHold: true);
            return;
        }
        if (!state.Connected)
        {
            ResetInternal(cancelBackHold: true);
            return;
        }
        if (!_wasConnected)
        {
            _wasConnected = true;
            _previousButtons = TvGamepadButtons.None;
            _previousState = state;
        }

        var buttons = state.Buttons;
        ProcessBackHold(buttons, nowMs);
        ProcessButtonEdges(buttons, nowMs);
        ProcessDirection(state, nowMs);
        ProcessTriggers(state, nowMs);
        ProcessRightStick(state, nowMs);
        _previousButtons = buttons;
        _previousState = state;
    }

    /// <summary>Clear all held state (focus loss, disable, controller disconnect).</summary>
    public void Reset() => ResetInternal(cancelBackHold: true);

    private void ResetInternal(bool cancelBackHold)
    {
        if (cancelBackHold && _backHeld)
        {
            _backHeld = false;
            _backCompleted = false;
            BackHoldCancelled?.Invoke(this, EventArgs.Empty);
        }
        _backHeld = false;
        _backCompleted = false;
        _wasConnected = false;
        _previousButtons = TvGamepadButtons.None;
        _previousState = default;
        _directionAction = null;
        _directionLabel = string.Empty;
        _leftTriggerHeld = false;
        _rightTriggerHeld = false;
        _rightStickHeld = false;
        _lastReportedProgress = -1;
        BackHoldProgress = 0;
    }

    private void ProcessBackHold(TvGamepadButtons buttons, long nowMs)
    {
        var down = (buttons & TvGamepadButtons.B) != 0;
        if (down && !_backHeld)
        {
            _backHeld = true;
            _backCompleted = false;
            _backDownAt = nowMs;
            _lastReportedProgress = -1;
            BackHoldProgress = 0;
            BackHoldStarted?.Invoke(this, EventArgs.Empty);
            BackHoldProgressChanged?.Invoke(this, new TvBackHoldProgressEventArgs(0, false));
        }
        if (!_backHeld) return;
        var progress = Math.Clamp((nowMs - _backDownAt) / (double)_exitHoldMs, 0, 1);
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
            return;
        }
        if (down) return;
        // Released before the long-press threshold: single Back tap.
        _backHeld = false;
        if (_backCompleted)
        {
            _backCompleted = false;
            return;
        }
        BackHoldProgress = 0;
        BackHoldCancelled?.Invoke(this, EventArgs.Empty);
        Raise(TvInputAction.Back, "B");
    }

    private void ProcessButtonEdges(TvGamepadButtons buttons, long nowMs)
    {
        // B is handled by the hold state machine so it can distinguish tap/long-press.
        var current = buttons & ~TvGamepadButtons.B;
        var previous = _previousButtons & ~TvGamepadButtons.B;
        var edges = current & ~previous;
        if (edges == TvGamepadButtons.None) return;
        RaiseIf(edges, TvGamepadButtons.A, TvInputAction.Select, "A");
        RaiseIf(edges, TvGamepadButtons.X, TvInputAction.Menu, "X");
        RaiseIf(edges, TvGamepadButtons.Y, TvInputAction.Info, "Y");
        RaiseIf(edges, TvGamepadButtons.Start, TvInputAction.PlayPause, "Start");
        RaiseIf(edges, TvGamepadButtons.Back, TvInputAction.Back, "View");
        RaiseIf(edges, TvGamepadButtons.LeftShoulder, TvInputAction.PrevEpisode, "LB");
        RaiseIf(edges, TvGamepadButtons.RightShoulder, TvInputAction.NextEpisode, "RB");
        _ = nowMs;
    }

    private void RaiseIf(TvGamepadButtons edges, TvGamepadButtons button, TvInputAction action, string label)
    {
        if ((edges & button) != 0) Raise(action, label);
    }

    private void ProcessDirection(TvGamepadState state, long nowMs)
    {
        var (action, label) = ResolveDirection(state);
        if (action is null)
        {
            _directionAction = null;
            _directionLabel = string.Empty;
            return;
        }
        if (_directionAction != action)
        {
            _directionAction = action;
            _directionLabel = label;
            Raise(action.Value, label);
            _nextDirectionRepeatAt = nowMs + RepeatInitialDelayMs;
            return;
        }
        if (nowMs < _nextDirectionRepeatAt) return;
        Raise(action.Value, _directionLabel);
        _nextDirectionRepeatAt += RepeatIntervalMs;
        if (_nextDirectionRepeatAt < nowMs) _nextDirectionRepeatAt = nowMs + RepeatIntervalMs;
    }

    private (TvInputAction? Action, string Label) ResolveDirection(TvGamepadState state)
    {
        if ((state.Buttons & TvGamepadButtons.DPadUp) != 0) return (TvInputAction.Up, "↑");
        if ((state.Buttons & TvGamepadButtons.DPadDown) != 0) return (TvInputAction.Down, "↓");
        if ((state.Buttons & TvGamepadButtons.DPadLeft) != 0) return (TvInputAction.Left, "←");
        if ((state.Buttons & TvGamepadButtons.DPadRight) != 0) return (TvInputAction.Right, "→");
        var deadzone = (short)Math.Round(Deadzone * short.MaxValue);
        var x = state.LeftThumbX;
        var y = state.LeftThumbY; // XInput: positive Y is up
        var horizontal = Math.Abs(x) >= deadzone;
        var vertical = Math.Abs(y) >= deadzone;
        if (!horizontal && !vertical) return (null, string.Empty);
        if (horizontal && (!vertical || Math.Abs(x) >= Math.Abs(y)))
            return x >= 0 ? (TvInputAction.Right, "Ls→") : (TvInputAction.Left, "Ls←");
        return y >= 0 ? (TvInputAction.Up, "Ls↑") : (TvInputAction.Down, "Ls↓");
    }

    private void ProcessTriggers(TvGamepadState state, long nowMs)
    {
        ProcessTrigger(state.LeftTrigger, ref _leftTriggerHeld, ref _nextLeftSeekAt, TvInputAction.SeekBack, "LT", nowMs);
        ProcessTrigger(state.RightTrigger, ref _rightTriggerHeld, ref _nextRightSeekAt, TvInputAction.SeekForward, "RT", nowMs);
    }

    private void ProcessTrigger(byte value, ref bool held, ref long nextRepeatAt, TvInputAction action, string label, long nowMs)
    {
        var pressed = value >= TriggerThreshold;
        if (pressed && !held)
        {
            held = true;
            Raise(action, label);
            nextRepeatAt = nowMs + SeekRepeatInitialDelayMs;
            return;
        }
        if (!pressed)
        {
            held = false;
            return;
        }
        if (nowMs < nextRepeatAt) return;
        Raise(action, label);
        nextRepeatAt += SeekRepeatIntervalMs;
        if (nextRepeatAt < nowMs) nextRepeatAt = nowMs + SeekRepeatIntervalMs;
    }

    private void ProcessRightStick(TvGamepadState state, long nowMs)
    {
        var deadzone = (short)Math.Round(Math.Max(0.5, Deadzone) * short.MaxValue);
        if (Math.Abs(state.RightThumbX) < deadzone)
        {
            _rightStickHeld = false;
            return;
        }
        var action = state.RightThumbX > 0 ? TvInputAction.SeekForward : TvInputAction.SeekBack;
        if (!_rightStickHeld)
        {
            _rightStickHeld = true;
            Raise(action, state.RightThumbX > 0 ? "Rs→" : "Rs←");
            _nextRightStickSeekAt = nowMs + SeekRepeatInitialDelayMs;
            return;
        }
        if (nowMs < _nextRightStickSeekAt) return;
        Raise(action, state.RightThumbX > 0 ? "Rs→" : "Rs←");
        _nextRightStickSeekAt += SeekRepeatIntervalMs;
        if (_nextRightStickSeekAt < nowMs) _nextRightStickSeekAt = nowMs + SeekRepeatIntervalMs;
    }

    private void Raise(TvInputAction action, string label) => ActionInvoked?.Invoke(this, new TvGamepadActionEventArgs(action, label));

    public void Dispose() => ResetInternal(cancelBackHold: true);
}