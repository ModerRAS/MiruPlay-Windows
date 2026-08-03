using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AudioDspSignalMathTests
{
    private const int SampleRateHz = 48_000;

    [Fact]
    public void DifferentLeftAndRightRulesProduceDifferentResponses()
    {
        var preset = new AudioDspPreset("stereo", "Stereo", Rules: [
            new(AudioDspChannelTarget.Left, [new(GainDb: -6, FrequencyHz: 1_000, Q: 1)]),
            new(AudioDspChannelTarget.Right, [new(GainDb: 6, FrequencyHz: 1_000, Q: 1)]),
        ]);

        var response = AudioDspSignalMath.SampleChannels(
            preset, AudioDspChannelLayout.Stereo, SampleRateHz);

        Assert.True(response[0].MagnitudeDbAt(1_000) < -5);
        Assert.True(response[1].MagnitudeDbAt(1_000) > 5);
    }

    [Theory]
    [InlineData(AudioDspFilterType.Peaking, 6, 1, 6)]
    [InlineData(AudioDspFilterType.LowPass, 0, 0.7071067811865476, -3.0103)]
    [InlineData(AudioDspFilterType.HighPass, 0, 0.7071067811865476, -3.0103)]
    [InlineData(AudioDspFilterType.BandPass, 0, 1, 0)]
    public void RbjFiltersHaveExpectedMagnitudeAtCenter(
        AudioDspFilterType type,
        double gainDb,
        double q,
        double expectedDb)
    {
        var coefficients = AudioDspSignalMath.DesignBiquad(
            new AudioDspBand(type, 1_000, gainDb, q), SampleRateHz);

        Assert.Equal(expectedDb, MagnitudeDb(coefficients, 1_000), 3);
    }

    [Fact]
    public void RbjNotchRejectsItsCenterFrequency()
    {
        var coefficients = AudioDspSignalMath.DesignBiquad(
            new AudioDspBand(AudioDspFilterType.Notch, 1_000, Q: 1), SampleRateHz);

        Assert.True(MagnitudeDb(coefficients, 1_000) < -200);
    }

    [Theory]
    [InlineData(AudioDspFilterType.LowShelf, 20, 5.9)]
    [InlineData(AudioDspFilterType.LowShelf, 20_000, 0.1)]
    [InlineData(AudioDspFilterType.HighShelf, 20, 0.1)]
    [InlineData(AudioDspFilterType.HighShelf, 20_000, 5.9)]
    public void RbjShelvesBoostOnlyTheirIntendedSide(
        AudioDspFilterType type,
        double frequencyHz,
        double expectedBoundaryDb)
    {
        var coefficients = AudioDspSignalMath.DesignBiquad(
            new AudioDspBand(type, 1_000, 6, 1), SampleRateHz);
        var magnitudeDb = MagnitudeDb(coefficients, frequencyHz);

        if (expectedBoundaryDb > 1)
            Assert.True(magnitudeDb > expectedBoundaryDb);
        else
            Assert.True(Math.Abs(magnitudeDb) < expectedBoundaryDb);
    }

    [Fact]
    public void MostSpecificChannelRuleWins()
    {
        var preset = new AudioDspPreset("routing", "Routing", Rules: [
            new(AudioDspChannelTarget.All, OutputGainDb: -1),
            new(AudioDspChannelTarget.Front, OutputGainDb: -3),
            new(AudioDspChannelTarget.Left, OutputGainDb: 6),
        ]);

        var response = AudioDspSignalMath.SampleChannels(
            preset, AudioDspChannelLayout.Surround51, SampleRateHz);

        Assert.Equal(6, response[0].MagnitudeDbAt(1_000), 6);
        Assert.Equal(-3, response[1].MagnitudeDbAt(1_000), 6);
        Assert.Equal(-1, response[2].MagnitudeDbAt(1_000), 6);
    }

    [Fact]
    public void LinearPhaseFirsAreSymmetricAndShareGroupDelay()
    {
        var preset = new AudioDspPreset(
            "stereo",
            "Stereo",
            PhaseMode: AudioDspPhaseMode.Linear,
            FirQuality: AudioDspFirQuality.Medium,
            Rules: [new(AudioDspChannelTarget.Left, [new(GainDb: -6)])]);

        var plans = AudioDspSignalMath.BuildLinearPhaseChannels(
            preset, AudioDspChannelLayout.Stereo, SampleRateHz);

        Assert.Equal(2_048, plans[0].Taps.Length);
        Assert.Equal(plans[0].GroupDelaySamples, plans[1].GroupDelaySamples);
        Assert.Equal(1_023, plans[0].GroupDelaySamples);
        Assert.NotEqual(plans[0].Taps[1_023], plans[1].Taps[1_023]);
        Assert.All(plans, plan => Assert.Equal(plan.Taps.Reverse(), plan.Taps));
    }

    [Fact]
    public void FirDesignerUsesRequestedTapCountAndExactMirrorSymmetry()
    {
        var plan = LinearPhaseFirDesigner.Design([0, -3, -6, -3, 0], SampleRateHz, 32);

        Assert.Equal(32, plan.Taps.Length);
        Assert.Equal(15, plan.GroupDelaySamples);
        Assert.Equal(plan.Taps.Reverse(), plan.Taps);
        Assert.All(plan.Taps, tap => Assert.True(float.IsFinite(tap)));
    }

    [Fact]
    public void StandardLayoutsUseStableChannelOrder()
    {
        Assert.Equal(["FC"], AudioDspChannelLayout.Mono.Channels);
        Assert.Equal(["FL", "FR"], AudioDspChannelLayout.Stereo.Channels);
        Assert.Equal(
            ["FL", "FR", "FC", "LFE", "SL", "SR"],
            AudioDspChannelLayout.Surround51.Channels);
        Assert.Equal(
            ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR"],
            AudioDspChannelLayout.Surround71.Channels);
    }

    [Theory]
    [MemberData(nameof(ChannelTargets))]
    public void ChannelTargetsResolveToStableLabels(
        AudioDspChannelTarget target,
        AudioDspChannelLayout layout,
        string[] expected)
    {
        Assert.Equal(expected, AudioDspSignalMath.ResolveTarget(target, layout));
    }

    [Fact]
    public void ItuStereoDownmixUsesFrontCenterLfeAndSurroundWeights()
    {
        var matrix = AudioDspSignalMath.BuildStereoDownmixMatrix(AudioDspChannelLayout.Surround71);
        var minus3Db = Math.Sqrt(0.5);

        Assert.Equal(1, matrix[0, 0]);
        Assert.Equal(1, matrix[1, 1]);
        Assert.Equal(minus3Db, matrix[0, 2], 12);
        Assert.Equal(minus3Db, matrix[1, 2], 12);
        Assert.Equal(0.5, matrix[0, 3]);
        Assert.Equal(0.5, matrix[1, 3]);
        Assert.Equal(minus3Db, matrix[0, 4], 12);
        Assert.Equal(minus3Db, matrix[1, 5], 12);
        Assert.Equal(minus3Db, matrix[0, 6], 12);
        Assert.Equal(minus3Db, matrix[1, 7], 12);
        Assert.Equal(0, matrix[1, 0]);
        Assert.Equal(0, matrix[0, 1]);
    }

    [Fact]
    public void ItuStereoDownmixSupportsMonoStereoAndSurround51Layouts()
    {
        var mono = AudioDspSignalMath.BuildStereoDownmixMatrix(AudioDspChannelLayout.Mono);
        var stereo = AudioDspSignalMath.BuildStereoDownmixMatrix(AudioDspChannelLayout.Stereo);
        var surround = AudioDspSignalMath.BuildStereoDownmixMatrix(AudioDspChannelLayout.Surround51);
        var minus3Db = Math.Sqrt(0.5);

        Assert.Equal(minus3Db, mono[0, 0], 12);
        Assert.Equal(minus3Db, mono[1, 0], 12);
        Assert.Equal(1, stereo[0, 0]);
        Assert.Equal(1, stereo[1, 1]);
        Assert.Equal(minus3Db, surround[0, 4], 12);
        Assert.Equal(minus3Db, surround[1, 5], 12);
    }

    [Theory]
    [InlineData(AudioDspOutputMode.AutoPreserve, "preserve")]
    [InlineData(AudioDspOutputMode.StereoDownmix, "stereo-downmix")]
    [InlineData(AudioDspOutputMode.HrtfBinaural, "hrtf")]
    public void OutputModesResolveToCompilerRouteMarkers(
        AudioDspOutputMode mode,
        string expected)
    {
        Assert.Equal(expected, AudioDspSignalMath.ResolveOutputRoute(mode));
    }

    public static TheoryData<AudioDspChannelTarget, AudioDspChannelLayout, string[]> ChannelTargets => new()
    {
        { AudioDspChannelTarget.All, AudioDspChannelLayout.Surround71, ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR"] },
        { AudioDspChannelTarget.Front, AudioDspChannelLayout.Surround71, ["FL", "FR"] },
        { AudioDspChannelTarget.CenterLfe, AudioDspChannelLayout.Surround71, ["FC", "LFE"] },
        { AudioDspChannelTarget.Surround, AudioDspChannelLayout.Surround71, ["SL", "SR", "BL", "BR"] },
        { AudioDspChannelTarget.Surround51, AudioDspChannelLayout.Surround51, ["FL", "FR", "FC", "LFE", "SL", "SR"] },
        { AudioDspChannelTarget.Surround71, AudioDspChannelLayout.Surround71, ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR"] },
        { AudioDspChannelTarget.Left, AudioDspChannelLayout.Surround71, ["FL"] },
        { AudioDspChannelTarget.Right, AudioDspChannelLayout.Surround71, ["FR"] },
        { AudioDspChannelTarget.Center, AudioDspChannelLayout.Surround71, ["FC"] },
        { AudioDspChannelTarget.Lfe, AudioDspChannelLayout.Surround71, ["LFE"] },
        { AudioDspChannelTarget.LeftSurround, AudioDspChannelLayout.Surround71, ["SL", "BL"] },
        { AudioDspChannelTarget.RightSurround, AudioDspChannelLayout.Surround71, ["SR", "BR"] },
        { AudioDspChannelTarget.Left, AudioDspChannelLayout.Mono, [] },
        { AudioDspChannelTarget.Center, AudioDspChannelLayout.Mono, ["FC"] },
    };

    private static double MagnitudeDb(BiquadCoefficients coefficients, double frequencyHz)
    {
        var omega = 2 * Math.PI * frequencyHz / SampleRateHz;
        var z1Real = Math.Cos(omega);
        var z1Imaginary = -Math.Sin(omega);
        var z2Real = Math.Cos(2 * omega);
        var z2Imaginary = -Math.Sin(2 * omega);
        var numeratorReal = coefficients.B0 + coefficients.B1 * z1Real + coefficients.B2 * z2Real;
        var numeratorImaginary = coefficients.B1 * z1Imaginary + coefficients.B2 * z2Imaginary;
        var denominatorReal = 1 + coefficients.A1 * z1Real + coefficients.A2 * z2Real;
        var denominatorImaginary = coefficients.A1 * z1Imaginary + coefficients.A2 * z2Imaginary;
        var numerator = Math.Sqrt(numeratorReal * numeratorReal + numeratorImaginary * numeratorImaginary);
        var denominator = Math.Sqrt(denominatorReal * denominatorReal + denominatorImaginary * denominatorImaginary);

        return 20 * Math.Log10(Math.Max(1e-12, numerator / denominator));
    }
}
