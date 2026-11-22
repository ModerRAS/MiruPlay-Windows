using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvGamepadServiceTests
{
    private sealed class Clock
    {
        public long Now { get; set; }
        public long Read() => Now;
    }

    private static (TvGamepadService Service, Clock Clock, List<(TvInputAction Action, string Label)> Actions) Create(
        byte triggerThreshold = TvGamepadService.DefaultTriggerThreshold,
        int exitHoldMs = 2000,
        double deadzone = 0.5)
    {
        var clock = new Clock();
        var service = new TvGamepadService(exitHoldMs, clock.Read)
        {
            Enabled = true,
            Deadzone = deadzone,
            TriggerThreshold = triggerThreshold,
        };
        var actions = new List<(TvInputAction, string)>();
        service.ActionInvoked += (_, args) => actions.Add((args.Action, args.Label));
        return (service, clock, actions);
    }

    private static TvGamepadState Pad(TvGamepadButtons buttons = TvGamepadButtons.None,
        byte lt = 0, byte rt = 0, short lx = 0, short ly = 0, short rx = 0) =>
        new(true, buttons, lt, rt, lx, ly, rx);

    [Fact]
    public void ButtonsMapToActionsOnRisingEdgeOnly()
    {
        var (service, clock, actions) = Create();
        service.Poll(Pad(TvGamepadButtons.A | TvGamepadButtons.X | TvGamepadButtons.Y |
                         TvGamepadButtons.Start | TvGamepadButtons.Back |
                         TvGamepadButtons.LeftShoulder | TvGamepadButtons.RightShoulder), clock.Now);
        Assert.Equal(
            [TvInputAction.Select, TvInputAction.Menu, TvInputAction.Info, TvInputAction.PlayPause,
             TvInputAction.Back, TvInputAction.PrevEpisode, TvInputAction.NextEpisode],
            actions.Select(item => item.Action).ToArray());

        service.Poll(Pad(TvGamepadButtons.A | TvGamepadButtons.X), clock.Now + 100);
        Assert.Equal(7, actions.Count);
        service.Dispose();
    }

    [Theory]
    [InlineData(TvGamepadButtons.DPadUp, TvInputAction.Up)]
    [InlineData(TvGamepadButtons.DPadDown, TvInputAction.Down)]
    [InlineData(TvGamepadButtons.DPadLeft, TvInputAction.Left)]
    [InlineData(TvGamepadButtons.DPadRight, TvInputAction.Right)]
    public void DPadMapsToDirection(TvGamepadButtons button, TvInputAction expected)
    {
        var (service, clock, actions) = Create();
        service.Poll(Pad(button), clock.Now);
        Assert.Equal(expected, Assert.Single(actions).Action);
        service.Dispose();
    }

    [Fact]
    public void LeftStickRespectsDeadzoneBoundary()
    {
        var (service, clock, actions) = Create(deadzone: 0.5);
        service.Poll(Pad(lx: 16_383), clock.Now); // just inside the 0.5 deadzone
        Assert.Empty(actions);
        service.Poll(Pad(lx: 16_384), clock.Now + 100); // boundary counts as pressed
        Assert.Equal(TvInputAction.Right, Assert.Single(actions).Action);
        service.Dispose();
    }

    [Fact]
    public void LeftStickUsesDominantAxis()
    {
        var (service, clock, actions) = Create(deadzone: 0.3);
        service.Poll(Pad(lx: 18_000, ly: 22_000), clock.Now);
        Assert.Equal(TvInputAction.Up, Assert.Single(actions).Action);

        var (service2, clock2, actions2) = Create(deadzone: 0.3);
        service2.Poll(Pad(lx: 22_000, ly: 18_000), clock2.Now);
        Assert.Equal(TvInputAction.Right, Assert.Single(actions2).Action);
        service.Dispose();
        service2.Dispose();
    }

    [Fact]
    public void HeldDirectionRepeatsAfterInitialDelayThenEveryNinetyMilliseconds()
    {
        var (service, clock, actions) = Create();
        service.Poll(Pad(TvGamepadButtons.DPadDown), clock.Now);
        Assert.Single(actions);

        clock.Now = TvGamepadService.RepeatInitialDelayMs - 1;
        service.Poll(Pad(TvGamepadButtons.DPadDown), clock.Now);
        Assert.Single(actions);

        clock.Now = TvGamepadService.RepeatInitialDelayMs;
        service.Poll(Pad(TvGamepadButtons.DPadDown), clock.Now);
        Assert.Equal(2, actions.Count);

        clock.Now = TvGamepadService.RepeatInitialDelayMs + TvGamepadService.RepeatIntervalMs;
        service.Poll(Pad(TvGamepadButtons.DPadDown), clock.Now);
        Assert.Equal(3, actions.Count);

        service.Poll(Pad(), clock.Now + 10); // release
        clock.Now += 1000;
        service.Poll(Pad(), clock.Now);
        Assert.Equal(3, actions.Count);
        service.Dispose();
    }

    [Fact]
    public void ShortBTapDispatchesBackAndCancelsExitHold()
    {
        var (service, clock, actions) = Create();
        var started = 0;
        var cancelled = 0;
        var completed = 0;
        service.BackHoldStarted += (_, _) => started++;
        service.BackHoldCancelled += (_, _) => cancelled++;
        service.BackHoldCompleted += (_, _) => completed++;

        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        Assert.Empty(actions);
        clock.Now = 400;
        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        service.Poll(Pad(), clock.Now + 10);

        Assert.Equal(TvInputAction.Back, Assert.Single(actions).Action);
        Assert.Equal(1, started);
        Assert.Equal(1, cancelled);
        Assert.Equal(0, completed);
        service.Dispose();
    }

    [Fact]
    public void LongBHoldCompletesExitAndDoesNotDispatchBack()
    {
        var (service, clock, actions) = Create(exitHoldMs: 2000);
        var completed = 0;
        service.BackHoldCompleted += (_, _) => completed++;

        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        clock.Now = 1500;
        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        Assert.Equal(0, completed);

        clock.Now = 2000;
        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        Assert.Equal(1, completed);

        clock.Now = 2500;
        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        service.Poll(Pad(), clock.Now + 10);
        Assert.Empty(actions);
        Assert.Equal(1, completed);
        service.Dispose();
    }

    [Fact]
    public void TriggersSeekWithThresholdAndRepeat()
    {
        var (service, clock, actions) = Create(triggerThreshold: 153);
        service.Poll(Pad(lt: 152), clock.Now);
        Assert.Empty(actions);

        clock.Now = 10;
        service.Poll(Pad(lt: 153), clock.Now);
        Assert.Equal(TvInputAction.SeekBack, Assert.Single(actions).Action);

        clock.Now = 10 + TvGamepadService.SeekRepeatInitialDelayMs;
        service.Poll(Pad(lt: 153), clock.Now);
        Assert.Equal(2, actions.Count);
        Assert.Equal(TvInputAction.SeekBack, actions[1].Action);

        service.Poll(Pad(rt: 255), clock.Now + 10);
        Assert.Contains(actions, item => item.Action == TvInputAction.SeekForward);
        service.Dispose();
    }

    [Fact]
    public void RightStickSeeksBeyondDeadzone()
    {
        var (service, clock, actions) = Create(deadzone: 0.5);
        service.Poll(Pad(rx: 10_000), clock.Now);
        Assert.Empty(actions);
        service.Poll(Pad(rx: 30_000), clock.Now + 100);
        Assert.Equal(TvInputAction.SeekForward, Assert.Single(actions).Action);
        service.Dispose();
    }

    [Fact]
    public void DisconnectedControllerProducesNoActionsAndCancelsHold()
    {
        var (service, clock, actions) = Create();
        var cancelled = 0;
        service.BackHoldCancelled += (_, _) => cancelled++;
        service.Poll(Pad(TvGamepadButtons.B), clock.Now);
        Assert.Empty(actions);
        service.Poll(new TvGamepadState(false, TvGamepadButtons.None), clock.Now + 100);
        Assert.Equal(1, cancelled);
        Assert.Empty(actions);

        // Hot-plug: reconnecting starts from a clean edge state.
        clock.Now += 1000;
        service.Poll(Pad(TvGamepadButtons.A), clock.Now);
        Assert.Equal(TvInputAction.Select, Assert.Single(actions).Action);
        service.Dispose();
    }

    [Fact]
    public void DisabledServiceProducesNoActions()
    {
        var (service, clock, actions) = Create();
        service.Enabled = false;
        service.Poll(Pad(TvGamepadButtons.A | TvGamepadButtons.B | TvGamepadButtons.DPadDown), clock.Now);
        clock.Now += 1000;
        service.Poll(Pad(TvGamepadButtons.DPadDown), clock.Now);
        Assert.Empty(actions);
        service.Dispose();
    }

    [Fact]
    public void DeadzoneIsClampedToSupportedRange()
    {
        var service = new TvGamepadService();
        service.Deadzone = 0.05;
        Assert.Equal(0.2, service.Deadzone);
        service.Deadzone = 0.95;
        Assert.Equal(0.8, service.Deadzone);
        service.Dispose();
    }

    [Theory]
    [InlineData(short.MinValue, (short)0, TvInputAction.Left)]
    [InlineData(short.MaxValue, (short)0, TvInputAction.Right)]
    [InlineData((short)0, short.MinValue, TvInputAction.Down)]
    [InlineData((short)0, short.MaxValue, TvInputAction.Up)]
    [InlineData(short.MinValue, short.MinValue, TvInputAction.Left)]
    [InlineData(short.MaxValue, short.MaxValue, TvInputAction.Right)]
    public void ExtremeLeftStickValuesDoNotOverflow(short lx, short ly, TvInputAction expected)
    {
        var (service, clock, actions) = Create();
        var error = Record.Exception(() => service.Poll(Pad(lx: lx, ly: ly), clock.Now));
        Assert.Null(error);
        Assert.Equal(expected, Assert.Single(actions).Action);
        service.Dispose();
    }

    [Fact]
    public void ExtremeRightStickValueDoesNotOverflow()
    {
        var (service, clock, actions) = Create();
        var error = Record.Exception(() => service.Poll(Pad(rx: short.MinValue), clock.Now));
        Assert.Null(error);
        Assert.Equal(TvInputAction.SeekBack, Assert.Single(actions).Action);
        service.Dispose();
    }

    [Theory]
    [InlineData(short.MinValue, short.MaxValue)]
    [InlineData(short.MaxValue, (short)-32767)]
    [InlineData((short)0, (short)0)]
    public void SdlYAxisInversionHandlesShortExtremes(short input, short expected) =>
        Assert.Equal(expected, SdlGamepadSource.InvertSdlY(input));
}
