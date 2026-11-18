namespace MiruPlay.Windows.Tv;

/// <summary>
/// Scripted virtual gamepad used only by the --tv-pad-demo switch. It feeds the
/// real <see cref="TvGamepadService"/> and therefore the real UI; production
/// builds never construct it.
/// </summary>
public sealed class TvGamepadDemoSource : IXInputSource
{
    private readonly Func<long> _clock;
    private readonly long _initialDelayMs;
    private long? _startedAt;
    private readonly List<DemoFrame> _frames;

    public TvGamepadDemoSource(Func<long>? clock = null, long initialDelayMs = 1_200)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _initialDelayMs = Math.Max(0, initialDelayMs);
        _frames = BuildTimeline();
    }

    public bool TryGetState(int userIndex, out TvGamepadState state)
    {
        state = default;
        if (userIndex != 0) return false;
        _startedAt ??= _clock();
        var elapsed = _clock() - _startedAt.Value;
        var index = elapsed - _initialDelayMs;
        if (index < 0)
        {
            state = new TvGamepadState(true, TvGamepadButtons.None);
            return true;
        }
        foreach (var frame in _frames)
        {
            if (index < frame.StartMs || index >= frame.StartMs + frame.DurationMs) continue;
            state = new TvGamepadState(true, frame.Buttons, LeftThumbX: frame.LeftThumbX, RightThumbX: frame.RightThumbX);
            return true;
        }
        state = new TvGamepadState(true, TvGamepadButtons.None);
        return true;
    }

    internal IReadOnlyList<DemoFrame> Timeline => _frames;

    internal sealed record DemoFrame(long StartMs, long DurationMs, TvGamepadButtons Buttons, short LeftThumbX = 0, short RightThumbX = 0);

    private static List<DemoFrame> BuildTimeline()
    {
        var frames = new List<DemoFrame>();
        long t = 0;
        void Press(TvGamepadButtons button, long durationMs = 180, long gapMs = 420)
        {
            frames.Add(new DemoFrame(t, durationMs, button));
            t += durationMs + gapMs;
        }
        long Wait(long ms)
        {
            t += ms;
            return t;
        }

        Press(TvGamepadButtons.DPadDown, 180, 220);
        Press(TvGamepadButtons.DPadRight, 160, 220);
        Press(TvGamepadButtons.DPadUp, 160, 260);
        Press(TvGamepadButtons.DPadLeft, 160, 300);   // navigation rail
        Press(TvGamepadButtons.DPadDown, 160, 300);   // library
        Press(TvGamepadButtons.A, 180, 900);          // open library
        Press(TvGamepadButtons.DPadRight, 160, 160);
        Press(TvGamepadButtons.DPadRight, 160, 260);
        Press(TvGamepadButtons.DPadDown, 160, 260);
        Press(TvGamepadButtons.A, 180, 1200);         // open detail
        Press(TvGamepadButtons.A, 180, 1600);         // resume playback
        Wait(2600);                                   // OSD auto-hides
        Press(TvGamepadButtons.DPadDown, 160, 180);   // show OSD, focus transport
        Press(TvGamepadButtons.DPadDown, 160, 180);   // chips row
        Press(TvGamepadButtons.DPadRight, 160, 220);
        Press(TvGamepadButtons.A, 180, 900);          // open drawer
        frames.Add(new DemoFrame(t, 700, TvGamepadButtons.None));
        t += 700 + 350;
        Press(TvGamepadButtons.B, 160, 260);          // close drawer
        Press(TvGamepadButtons.B, 160, 260);          // hide OSD
        Press(TvGamepadButtons.B, 160, 900);          // leave playback
        Press(TvGamepadButtons.DPadLeft, 160, 260);   // navigation rail
        Press(TvGamepadButtons.DPadDown, 160, 160);   // library
        Press(TvGamepadButtons.DPadDown, 160, 160);   // search
        Press(TvGamepadButtons.DPadDown, 160, 180);   // settings
        Press(TvGamepadButtons.A, 180, 1600);         // open settings
        // Left-stick navigation inside settings to demonstrate stick mapping.
        frames.Add(new DemoFrame(t, 700, TvGamepadButtons.None, LeftThumbX: 30_000));
        t += 700 + 500;
        return frames;
    }
}
