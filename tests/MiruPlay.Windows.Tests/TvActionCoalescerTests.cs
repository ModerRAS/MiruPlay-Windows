using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvActionCoalescerTests
{
    [Fact]
    public void SameActionFromBothDevicesWithinWindowDispatchesOnce()
    {
        var coalescer = new TvActionCoalescer();
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Left, TvInputOrigin.Keyboard, 1_000));
        Assert.False(coalescer.ShouldDispatch(TvInputAction.Left, TvInputOrigin.Gamepad, 1_030));
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Left, TvInputOrigin.Gamepad, 1_070));
    }

    [Fact]
    public void RepeatedPressFromTheSameDeviceIsNotSuppressed()
    {
        var coalescer = new TvActionCoalescer();
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Right, TvInputOrigin.Gamepad, 1_000));
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Right, TvInputOrigin.Gamepad, 1_010));
    }

    [Fact]
    public void DifferentActionsAreIndependent()
    {
        var coalescer = new TvActionCoalescer();
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Up, TvInputOrigin.Keyboard, 1_000));
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Down, TvInputOrigin.Gamepad, 1_005));
        Assert.True(coalescer.ShouldDispatch(TvInputAction.Select, TvInputOrigin.Gamepad, 1_006));
    }
}
