using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvLaunchArgumentsTests
{
    [Fact]
    public void PlainTvSwitchStartsTvWithoutDemoBadge()
    {
        var launch = TvLaunchArguments.Parse(["--tv"]);
        Assert.True(launch.StartTv);
        Assert.False(launch.DemoBadge);
    }

    [Fact]
    public void DemoSwitchEnablesBadgeAndStartsTv()
    {
        var launch = TvLaunchArguments.Parse(["--tv-demo"]);
        Assert.True(launch.StartTv);
        Assert.True(launch.DemoBadge);
    }

    [Fact]
    public void TvAndDemoTogetherKeepBadgeOnlyBecauseOfDemoSwitch()
    {
        var launch = TvLaunchArguments.Parse(["--tv", "--tv-demo"]);
        Assert.True(launch.StartTv);
        Assert.True(launch.DemoBadge);
    }

    [Fact]
    public void PadDemoSwitchEnablesDemoBadgeAndSdlDemoSourceFlag()
    {
        var launch = TvLaunchArguments.Parse(["--tv-pad-demo"]);
        Assert.True(launch.StartTv);
        Assert.True(launch.DemoBadge);
        Assert.True(launch.GamepadDemo);
    }

    [Fact]
    public void GamepadDemoFlagIsOffForPlainTvAndKeyboardDemo()
    {
        Assert.False(TvLaunchArguments.Parse(["--tv"]).GamepadDemo);
        Assert.False(TvLaunchArguments.Parse(["--tv-demo"]).GamepadDemo);
    }

    [Fact]
    public void NoTvSwitchDoesNotStartTvMode()
    {
        var launch = TvLaunchArguments.Parse(["--background"]);
        Assert.False(launch.StartTv);
        Assert.False(launch.DemoBadge);
    }

    [Fact]
    public void SwitchesAreCaseInsensitive()
    {
        var launch = TvLaunchArguments.Parse(["--TV", "--TV-DEMO"]);
        Assert.True(launch.StartTv);
        Assert.True(launch.DemoBadge);
    }
}
