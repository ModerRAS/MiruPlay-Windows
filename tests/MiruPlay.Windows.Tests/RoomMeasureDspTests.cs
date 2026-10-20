using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RoomMeasureDspTests
{
    [Fact]
    public void FftRoundtripPreservesSignal()
    {
        var random = new Random(42);
        var signal = Enumerable.Range(0, 1_000).Select(_ => random.NextDouble() - 0.5).ToArray();

        var (re, im) = RoomMeasureFft.Rfft(signal, 1_024);
        var back = RoomMeasureFft.Irfft(re, im, 1_024);

        Assert.Equal(signal, back[..1_000], new DoubleToleranceComparer(1e-9));
    }

    [Fact]
    public void SweepDeconvolutionRecoversSyntheticImpulseResponse()
    {
        const int fs = 16_000;
        var sweep = RoomMeasureSweep.Generate(20, 8_000, 0.5, fs).Sweep;
        var random = new Random(7);
        var ir = new double[2_000];
        ir[0] = 1;
        for (var i = 1; i < ir.Length; i++) ir[i] = (random.NextDouble() - 0.5) * Math.Exp(-i / 300.0);
        var recorded = RoomMeasureFft.FftConvolve(sweep, ir);

        var recovered = RoomMeasureSweep.Deconvolve(recorded, sweep);

        Assert.Equal(0, PeakIndex(recovered));
        Assert.True(RoomMeasureSweep.CosineSimilarity(ir[60..], recovered[60..Math.Min(recovered.Length, ir.Length)]) > 0.9);
    }

    [Fact]
    public void DriftEstimateFindsCleanRecordingNearZero()
    {
        const int fs = 16_000;
        var sweep = RoomMeasureSweep.Generate(20, 8_000, 4.0, fs).Sweep;
        var random = new Random(11);
        var ir = new double[2_000];
        ir[0] = 1;
        for (var i = 1; i < ir.Length; i++) ir[i] = (random.NextDouble() - 0.5) * Math.Exp(-i / 200.0);
        var recorded = RoomMeasureSweep.ApplyDrift(RoomMeasureFft.FftConvolve(sweep, ir), 10);

        var estimate = RoomMeasureSweep.EstimateDrift(recorded, sweep, irSampleCount: 2_000);

        Assert.True(Math.Abs(estimate.EstimatedPpm - 10) < 3, $"estimated {estimate.EstimatedPpm} ppm");
    }

    [Fact]
    public void TailSimilaritySeparatesMatchingTailsFromDecorrelatedOnes()
    {
        var random = new Random(23);
        var irA = new double[2_000];
        var irB = new double[2_000];
        irA[0] = 1;
        irB[0] = 1;
        for (var i = 1; i < irA.Length; i++)
        {
            var tail = (random.NextDouble() - 0.5) * Math.Exp(-i / 400.0);
            irA[i] = tail;
            irB[i] = tail;
        }
        var other = new double[2_000];
        other[0] = 1;
        for (var i = 1; i < other.Length; i++) other[i] = (random.NextDouble() - 0.5) * Math.Exp(-i / 400.0);

        Assert.True(RoomMeasureSweep.TailSimilarity(irA, irB) > 0.95);
        Assert.True(RoomMeasureSweep.TailSimilarity(irA, other) < 0.8);
    }

    [Fact]
    public void InverseEnvelopeIsProportionalToFrequencyNotItsSquareRoot()
    {
        var generated = RoomMeasureSweep.Generate(20, 8_000, 1.0, 16_000);

        // The inverse is time-reversed: its head holds the sweep's high-
        // frequency end, so a ∝f envelope makes head samples large and tail
        // samples small (a √f bug would only separate them ~20x for f2/f1=400).
        var early = Math.Abs(generated.Inverse[100]);
        var late = Math.Abs(generated.Inverse[^100]);
        Assert.True(early > late * 10, $"early {early}, late {late}");
        Assert.Equal(generated.Sweep.Length, generated.Inverse.Length);
        Assert.Equal(1, generated.Inverse.Max(Math.Abs), 9);
    }

    [Fact]
    public void AutoEqCutOnlyFitReducesRmsWithoutBoost()
    {
        const int fs = 48_000;
        var freqs = RoomMeasureAnalysis.LogFreqGrid(20, 500, 12);
        var target = new double[freqs.Length];
        var measured = freqs.Select(f =>
        {
            var d = Math.Log2(f / 100) / 0.5;
            return 6 * Math.Exp(-d * d);
        }).ToArray();

        var result = RoomMeasureAutoEq.FitPeq(measured, target, freqs, new RoomMeasureAutoEq.PeqFitConfig(), fs);

        Assert.NotEmpty(result.Bands);
        Assert.All(result.Bands, band => Assert.Equal(AudioDspFilterType.Peaking, band.Type));
        Assert.All(result.Bands, band => Assert.True(band.GainDb <= 1e-9, $"boost {band.GainDb}"));
        Assert.True(result.RmsDeviationDb < 6 / Math.Sqrt(2), $"rms {result.RmsDeviationDb}");
        Assert.True(result.OverallMaxBoostDb <= 1e-9);
    }

    [Fact]
    public void MeasuredPresetFactoryGatesInvalidMeasurements()
    {
        var invalid = new RoomMeasureAutoEq.MeasuredBands([], false, "dual-sweep tails disagree");

        Assert.Throws<ArgumentException>(() => RoomMeasureAutoEq.BuildPreset(invalid, 1_700_000_000_000));

        var bands = new RoomMeasureAutoEq.MeasuredBands(
            [new AudioDspBand(AudioDspFilterType.Peaking, 100, -6, 2)], true);
        var preset = RoomMeasureAutoEq.BuildPreset(bands, 1_700_000_000_000);
        Assert.StartsWith(RoomMeasureAutoEq.PresetIdPrefix, preset.Id);
        Assert.Single(preset.Rules!);
        var config = RoomMeasureAutoEq.ApplyToConfig(AudioDspConfig.Neutral(), bands, 1_700_000_000_000);
        Assert.Equal(preset.Id, config.SelectedPresetId);
        Assert.Contains(config.Presets!, item => item.Id == preset.Id);
    }

    private static int PeakIndex(double[] ir)
    {
        var best = 0;
        for (var i = 0; i < ir.Length; i++)
            if (Math.Abs(ir[i]) > Math.Abs(ir[best])) best = i;
        return best;
    }

    private sealed class DoubleToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double value) => 0;
    }
}
