using System.Globalization;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Maps <see cref="AudioBalanceLevel"/> to an mpv/FFmpeg filter chain.
///
/// Android uses a custom Exo/Media3 PCM AGC; Windows uses the platform equivalent
/// <c>dynaudnorm</c> (streaming RMS normalisation with a linked peak ceiling). The
/// numeric parameters are intentionally monotonic by level rather than bit-identical:
/// higher levels allow more boost and react faster.
/// </summary>
public sealed record AudioBalanceFilter(AudioBalanceLevel Level, string FilterValue);

public static class AudioBalanceFilterCompiler
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Filter inserted after the DSP/PEQ chain; null when balance is OFF.</summary>
    public static AudioBalanceFilter? Compile(AudioBalanceLevel level, int sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        if (!level.IsEnabled()) return null;
        var parameters = ForLevel(level);
        var filter = "dynaudnorm=" +
            $"f={parameters.FrameMs.ToString(Invariant)}" +
            $":g={parameters.GaussianWindow.ToString(Invariant)}" +
            $":m={parameters.MaxBoostFactor.ToString("0.###", Invariant)}" +
            $":r={parameters.TargetRms.ToString("0.###", Invariant)}" +
            $":p={parameters.PeakCeiling.ToString("0.###", Invariant)}" +
            ":n=1";
        return new AudioBalanceFilter(level, filter);
    }

    internal static AudioBalanceParameters ForLevel(AudioBalanceLevel level) => level switch
    {
        AudioBalanceLevel.Low => new AudioBalanceParameters(FrameMs: 300, GaussianWindow: 15, MaxBoostFactor: 2.0, TargetRms: 0.1, PeakCeiling: 0.95),
        AudioBalanceLevel.Medium => new AudioBalanceParameters(FrameMs: 200, GaussianWindow: 11, MaxBoostFactor: 3.2, TargetRms: 0.1, PeakCeiling: 0.95),
        AudioBalanceLevel.High => new AudioBalanceParameters(FrameMs: 120, GaussianWindow: 9, MaxBoostFactor: 5.0, TargetRms: 0.1, PeakCeiling: 0.95),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "OFF has no balance parameters"),
    };
}

public sealed record AudioBalanceParameters(
    int FrameMs,
    int GaussianWindow,
    double MaxBoostFactor,
    double TargetRms,
    double PeakCeiling);
