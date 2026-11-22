using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RifeEnhancementUxTests
{
    private static RifeEnhancementStatus Status(
        bool installed = true,
        bool ready = false,
        string? reason = RifeEnhancementReasons.SelftestRequired,
        string source = "bundled",
        RifeSelfTestResult? selftest = null) =>
        new(installed, ready, installed ? "2026.11" : null, source, installed ? @"C:\pack" : null, 1, reason, selftest, []);

    private static RifeSelfTestResult Result(bool ok, string? errorKey = null, string? gpu = "RTX 5070 Ti") =>
        new(ok, ok ? 60 : null, gpu, errorKey, null, 0, "2026.11", "480");

    [Fact]
    public void ReadyEnablesImmediately()
    {
        var decision = RifeEnhancementUx.Decide(Status(ready: true, reason: null));
        Assert.Equal(RifeEnableAction.Enable, decision.Action);
        Assert.Equal("tv.settings.nextPlaybackHint", decision.MessageKey);
    }

    [Fact]
    public void NotInstalledUsesLiteEditionMessage()
    {
        var decision = RifeEnhancementUx.Decide(Status(installed: false, reason: RifeEnhancementReasons.NotInstalled));
        Assert.Equal(RifeEnableAction.RejectNotInstalled, decision.Action);
        Assert.Equal("tv.settings.rifeLiteHint", decision.MessageKey);
        Assert.Equal("tv.settings.rifeStatusNotInstalled", RifeEnhancementUx.StateKey(Status(installed: false)));
    }

    [Fact]
    public void InstalledWithoutSelftestStartsSelftest()
    {
        var decision = RifeEnhancementUx.Decide(Status());
        Assert.Equal(RifeEnableAction.RunSelftest, decision.Action);
        Assert.Equal("tv.settings.rifeSelftestRunning", decision.MessageKey);
        Assert.Equal("tv.settings.rifeStatusNotTested", RifeEnhancementUx.StateKey(Status()));
    }

    [Fact]
    public void FailedSelftestReportsSpecificReason()
    {
        var status = Status(reason: RifeEnhancementReasons.SelftestTimeout, selftest: Result(false, RifeEnhancementReasons.SelftestTimeout));
        var decision = RifeEnhancementUx.Decide(status);
        Assert.Equal(RifeEnableAction.RejectSelftestFailed, decision.Action);
        Assert.Equal(RifeEnhancementReasons.SelftestTimeout, decision.MessageKey);
        Assert.Equal("tv.settings.rifeStatusFailed", RifeEnhancementUx.StateKey(status));
    }

    [Fact]
    public void SourceAndReadyStateKeysMatchPackSource()
    {
        Assert.Equal("tv.settings.rifeSourceBundled", RifeEnhancementUx.SourceKey(Status(source: "bundled")));
        Assert.Equal("tv.settings.rifeSourceInstalled", RifeEnhancementUx.SourceKey(Status(source: "installed")));
        Assert.Equal("tv.settings.rifeStatusPassed", RifeEnhancementUx.StateKey(Status(ready: true, reason: null)));
    }

    [Fact]
    public async Task SuccessfulSelftestKeepsRifeEnabled()
    {
        var calls = 0;
        var flow = new RifeEnableFlow();
        var result = await flow.EnableAsync(
            Status(),
            new AppSettings(FrameInterpolationMode: "mpv_builtin"),
            () => { calls++; return Task.FromResult(Result(true)); });
        Assert.Equal(1, calls);
        Assert.Equal(MpvFrameInterpolationModes.Rife, result.FrameInterpolationMode);
        Assert.True(result.SelfTest?.Ok);
        Assert.False(result.Duplicate);
    }

    [Fact]
    public async Task FailedSelftestRevertsOriginalMode()
    {
        var flow = new RifeEnableFlow();
        var result = await flow.EnableAsync(
            Status(),
            new AppSettings(FrameInterpolationMode: "mpv_builtin"),
            () => Task.FromResult(Result(false, RifeEnhancementReasons.SelftestSlow)));
        Assert.Equal("mpv_builtin", result.FrameInterpolationMode);
        Assert.Equal(RifeEnhancementReasons.SelftestSlow, result.ErrorKey);
    }

    [Fact]
    public async Task DuplicateSelftestIsIgnoredWhileRunning()
    {
        var flow = new RifeEnableFlow();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RifeSelfTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = flow.EnableAsync(Status(), new AppSettings(FrameInterpolationMode: "mpv_builtin"), () =>
        {
            calls++;
            started.TrySetResult();
            return release.Task;
        });
        await started.Task;
        var second = await flow.EnableAsync(Status(), new AppSettings(FrameInterpolationMode: "mpv_builtin"), () =>
        {
            calls++;
            return Task.FromResult(Result(true));
        });
        Assert.True(second.Duplicate);
        Assert.Equal(1, calls);
        release.SetResult(Result(true));
        var firstResult = await first;
        Assert.Equal(MpvFrameInterpolationModes.Rife, firstResult.FrameInterpolationMode);
        Assert.False(flow.IsRunning);
    }
}
