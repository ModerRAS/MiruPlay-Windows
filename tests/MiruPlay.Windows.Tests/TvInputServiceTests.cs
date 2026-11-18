using System.Windows.Input;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvInputServiceTests
{
    private sealed class Clock
    {
        public long Now { get; set; }
        public long Read() => Now;
    }

    private static (TvInputService Service, Clock Clock, List<TvInputAction> Actions) Create(int backHoldMs = 2000)
    {
        var clock = new Clock();
        var service = new TvInputService(TvRemotePreset.General, backHoldMs, clock.Read, startTimer: false);
        var actions = new List<TvInputAction>();
        service.ActionInvoked += (_, args) => actions.Add(args.Action);
        return (service, clock, actions);
    }

    [Fact]
    public void NavigationKeyDispatchesImmediately()
    {
        var (service, _, actions) = Create();
        service.HandleKeyDown(Key.Down, ModifierKeys.None);
        Assert.Equal([TvInputAction.Down], actions);
        service.Dispose();
    }

    [Fact]
    public void UnmappedKeyIsIgnored()
    {
        var (service, _, actions) = Create();
        Assert.False(service.HandleKeyDown(Key.F24, ModifierKeys.None));
        Assert.Empty(actions);
        service.Dispose();
    }

    [Fact]
    public void OperatingSystemAutoRepeatIsIgnoredAndKeyUpStopsHeldState()
    {
        var (service, clock, actions) = Create();
        service.HandleKeyDown(Key.Down, ModifierKeys.None);
        service.HandleKeyDown(Key.Down, ModifierKeys.None); // OS repeat, no key-up yet

        Assert.Equal([TvInputAction.Down], actions);

        service.HandleKeyUp(Key.Down);
        service.HandleKeyDown(Key.Down, ModifierKeys.None);
        Assert.Equal([TvInputAction.Down, TvInputAction.Down], actions);
        service.Dispose();
    }

    [Fact]
    public void HeldDirectionRepeatsAfterInitialDelayThenEveryNinetyMilliseconds()
    {
        var (service, clock, actions) = Create();
        service.HandleKeyDown(Key.Right, ModifierKeys.None);
        Assert.Single(actions);

        clock.Now = TvInputService.RepeatingInitialDelayMs - 1;
        service.Pump(clock.Now);
        Assert.Single(actions);

        clock.Now = TvInputService.RepeatingInitialDelayMs;
        service.Pump(clock.Now);
        Assert.Equal(2, actions.Count);

        clock.Now = TvInputService.RepeatingInitialDelayMs + TvInputService.RepeatingIntervalMs - 1;
        service.Pump(clock.Now);
        Assert.Equal(2, actions.Count);

        clock.Now = TvInputService.RepeatingInitialDelayMs + TvInputService.RepeatingIntervalMs;
        service.Pump(clock.Now);
        Assert.Equal(3, actions.Count);

        service.HandleKeyUp(Key.Right);
        clock.Now += 5_000;
        service.Pump(clock.Now);
        Assert.Equal(3, actions.Count);
        service.Dispose();
    }

    [Fact]
    public void ShortBackTapIsDispatchedOnReleaseAndCancelsExitHold()
    {
        var (service, clock, actions) = Create();
        var started = 0;
        var cancelled = 0;
        var completed = 0;
        service.BackHoldStarted += (_, _) => started++;
        service.BackHoldCancelled += (_, _) => cancelled++;
        service.BackHoldCompleted += (_, _) => completed++;

        service.HandleKeyDown(Key.Escape, ModifierKeys.None);
        clock.Now = 400;
        service.Pump(clock.Now);
        Assert.Empty(actions);
        Assert.InRange(service.BackHoldProgress, 0.15, 0.25);

        service.HandleKeyUp(Key.Escape);
        Assert.Equal([TvInputAction.Back], actions);
        Assert.Equal(1, started);
        Assert.Equal(1, cancelled);
        Assert.Equal(0, completed);
        Assert.False(service.IsBackHeld);
        service.Dispose();
    }

    [Fact]
    public void LongBackHoldCompletesOnceAndDoesNotDispatchBackOnRelease()
    {
        var (service, clock, actions) = Create(backHoldMs: 2000);
        var completed = 0;
        service.BackHoldCompleted += (_, _) => completed++;

        service.HandleKeyDown(Key.Escape, ModifierKeys.None);
        clock.Now = 1500;
        service.Pump(clock.Now);
        Assert.Equal(0, completed);

        clock.Now = 2000;
        service.Pump(clock.Now);
        Assert.Equal(1, completed);

        clock.Now = 2500;
        service.Pump(clock.Now);
        Assert.Equal(1, completed);

        service.HandleKeyUp(Key.Escape);
        Assert.Empty(actions);
        service.Dispose();
    }

    [Fact]
    public void BackHoldProgressIsReportedForTheOverlay()
    {
        var (service, clock, _) = Create(backHoldMs: 2000);
        var progress = new List<double>();
        service.BackHoldProgressChanged += (_, args) => progress.Add(args.Progress);

        service.HandleKeyDown(Key.Escape, ModifierKeys.None);
        clock.Now = 1000;
        service.Pump(clock.Now);
        clock.Now = 2000;
        service.Pump(clock.Now);

        Assert.Contains(progress, value => value is > 0.4 and < 0.6);
        Assert.Contains(progress, value => value >= 0.99);
        service.Dispose();
    }

    [Fact]
    public void ResetClearsHeldKeysAndTimers()
    {
        var (service, clock, actions) = Create();
        service.HandleKeyDown(Key.Left, ModifierKeys.None);
        service.Reset();
        clock.Now = 10_000;
        service.Pump(clock.Now);
        Assert.Single(actions);
        service.Dispose();
    }
}
