using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public enum RifeEnableAction
{
    Enable,
    RunSelftest,
    RejectNotInstalled,
    RejectSelftestFailed,
}

public sealed record RifeEnableDecision(RifeEnableAction Action, string MessageKey, string? ReasonKey = null);

public sealed record RifeEnableResult(
    string FrameInterpolationMode,
    bool Duplicate,
    RifeSelfTestResult? SelfTest,
    string? ErrorKey,
    RifeEnableAction Action);

/// <summary>Pure presentation/flow rules for the TV RIFE enable button.</summary>
public static class RifeEnhancementUx
{
    public static RifeEnableDecision Decide(RifeEnhancementStatus status)
    {
        if (status.Ready) return new(RifeEnableAction.Enable, "tv.settings.nextPlaybackHint");
        if (!status.Installed)
            return new(RifeEnableAction.RejectNotInstalled, "tv.settings.rifeLiteHint", RifeEnhancementReasons.NotInstalled);
        var reason = status.ReasonKey ?? RifeEnhancementReasons.SelftestFailed;
        if (reason == RifeEnhancementReasons.SelftestRequired || status.SelfTest is null)
            return new(RifeEnableAction.RunSelftest, "tv.settings.rifeSelftestRunning");
        return new(RifeEnableAction.RejectSelftestFailed, reason, reason);
    }

    public static string SourceKey(RifeEnhancementStatus status) => !status.Installed
        ? "tv.settings.rifeStatusNotInstalled"
        : status.Source == "bundled"
            ? "quality.rifeSourceBundled"
            : "quality.rifeSourceInstalled";

    public static string StateKey(RifeEnhancementStatus status)
    {
        if (!status.Installed) return "tv.settings.rifeStatusNotInstalled";
        if (status.Ready) return "tv.settings.rifeStatusPassed";
        return status.ReasonKey == RifeEnhancementReasons.SelftestRequired || status.SelfTest is null
            ? "tv.settings.rifeStatusNotTested"
            : "tv.settings.rifeStatusFailed";
    }

    public static string StatusText(RifeEnhancementStatus status) => !status.Installed
        ? AppStrings.Get("tv.settings.rifeStatusNotInstalled")
        : AppStrings.Get(SourceKey(status)) + "  ·  " + AppStrings.Get(StateKey(status));
}

/// <summary>Serializes the TV auto self-test so a second click cannot start another one.</summary>
public sealed class RifeEnableFlow
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task<RifeEnableResult> EnableAsync(
        RifeEnhancementStatus status,
        AppSettings settings,
        Func<Task<RifeSelfTestResult>> runSelftest)
    {
        var decision = RifeEnhancementUx.Decide(status);
        switch (decision.Action)
        {
            case RifeEnableAction.Enable:
                return new RifeEnableResult(MpvFrameInterpolationModes.Rife, false, null, null, decision.Action);
            case RifeEnableAction.RejectNotInstalled:
            case RifeEnableAction.RejectSelftestFailed:
                return new RifeEnableResult(settings.FrameInterpolationMode, false, null, decision.ReasonKey, decision.Action);
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new RifeEnableResult(settings.FrameInterpolationMode, true, null, null, decision.Action);
        try
        {
            var result = await runSelftest().ConfigureAwait(false);
            return result.Ok
                ? new RifeEnableResult(MpvFrameInterpolationModes.Rife, false, result, null, decision.Action)
                : new RifeEnableResult(
                    settings.FrameInterpolationMode,
                    false,
                    result,
                    result.ErrorKey ?? RifeEnhancementReasons.SelftestFailed,
                    decision.Action);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
