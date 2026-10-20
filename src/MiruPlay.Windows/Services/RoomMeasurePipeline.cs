namespace MiruPlay.Windows.Services;

/// <summary>
/// Measurement pipeline facade: deconvolve → response → mic correction →
/// REW smoothing → target → auto-fit, plus the fail-closed clock-drift gate.
/// A single sweep cannot verify its own drift estimate, so a trustworthy
/// acoustic measurement REQUIRES a dual sweep with agreeing estimates; a
/// rejected measurement reports Valid = false and its bands MUST NOT be
/// applied. Loopback/shared-clock imports skip drift estimation explicitly.
/// </summary>
public static class RoomMeasurePipeline
{
    public sealed record DriftCheck(
        double EstimatedPpm,
        double CompensatedSharpness,
        bool Consistent,
        double PpmSpread,
        IReadOnlyList<double> Estimates,
        double TailSimilarity);

    public sealed record Measurement(
        int Fs,
        double[] Ir,
        double[] ResponseDb,
        double[] SmoothedDb,
        double[] Freqs,
        double[] TargetDb,
        RoomMeasureAutoEq.PeqFitResult Fit,
        DriftCheck Drift,
        bool Valid,
        string? InvalidReason);

    public static Measurement Measure(
        IReadOnlyList<double[]> recordings,
        IReadOnlyList<RoomMeasureSweep.LogSweep> sweeps,
        int fs,
        double irLengthS = 1.0,
        int gridPerOctave = 48,
        double gridLoHz = 10.0,
        double gridHiHz = 24_000.0,
        RoomMeasureAutoEq.PeqFitConfig? fitConfig = null,
        double ppmTolerance = 1.0,
        double driftSearchRangePpm = 15.0,
        double minTailSimilarity = 0.0,
        double minSharpness = 0.0,
        bool assumeSharedClock = false,
        MicCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        ArgumentNullException.ThrowIfNull(sweeps);
        if (recordings.Count != sweeps.Count) throw new ArgumentException("one recording per sweep required");
        if (recordings.Count == 0) throw new ArgumentException("at least one sweep required");
        fitConfig ??= new RoomMeasureAutoEq.PeqFitConfig();

        var irLen = (int)(irLengthS * fs);
        var freqs = RoomMeasureAnalysis.LogFreqGrid(gridLoHz, gridHiHz, gridPerOctave);
        if (assumeSharedClock)
            return MeasureSharedClock(recordings, sweeps, fs, irLen, freqs, fitConfig, calibration);

        var estimates = new RoomMeasureSweep.DriftEstimate[recordings.Count];
        Parallel.For(0, recordings.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(recordings.Count, 2) },
            i => estimates[i] = RoomMeasureSweep.EstimateDrift(
                recordings[i], sweeps[i].Sweep, irLen, searchRangePpm: driftSearchRangePpm));
        var spread = 0.0;
        for (var i = 1; i < estimates.Length; i++)
            spread = Math.Max(spread, Math.Abs(estimates[i].EstimatedPpm - estimates[0].EstimatedPpm));
        var meanPpm = estimates.Average(estimate => estimate.EstimatedPpm);
        var consistent = estimates.Length >= 2 && spread <= ppmTolerance;

        var ir = CompensatedIr(recordings[0], sweeps[0].Sweep, meanPpm, irLen);
        var tailSim = double.NaN;
        if (estimates.Length >= 2)
        {
            var irB = CompensatedIr(recordings[1], sweeps[1].Sweep, estimates[1].EstimatedPpm, irLen);
            tailSim = RoomMeasureSweep.TailSimilarity(ir, irB);
        }
        var drift = new DriftCheck(
            meanPpm, estimates[0].CompensatedSharpness, consistent, spread,
            estimates.Select(estimate => estimate.EstimatedPpm).ToList(), tailSim);
        var (responseDb, smoothedDb, targetDb, fit) = Analyze(ir, freqs, fs, fitConfig, calibration);

        string? invalidReason = null;
        if (estimates.Length < 2)
            invalidReason = "single sweep cannot verify clock drift; a dual sweep is required";
        else if (!consistent)
            invalidReason = $"clock-drift estimates disagree across sweeps (spread {spread:0.00} ppm > {ppmTolerance})";
        else if (Math.Abs(meanPpm) >= driftSearchRangePpm - 0.1)
            invalidReason = $"clock-drift estimate hit the ±{driftSearchRangePpm:0} ppm search boundary; drift is too large to compensate";
        else if (minTailSimilarity > 0 && tailSim < minTailSimilarity)
            invalidReason = $"compensated room tails disagree (similarity {tailSim:0.000} < {minTailSimilarity})";
        else if (drift.CompensatedSharpness < minSharpness)
            invalidReason = $"compensated IR sharpness {drift.CompensatedSharpness:0.0} below gate {minSharpness}";

        return new Measurement(fs, ir, responseDb, smoothedDb, freqs, targetDb, fit, drift,
            invalidReason is null, invalidReason);
    }

    private static double[] CompensatedIr(double[] recorded, double[] sweep, double ppm, int irLen)
    {
        var reference = Math.Abs(ppm) > 0.05 ? RoomMeasureSweep.ApplyDrift(sweep, ppm) : sweep;
        var ir = RoomMeasureSweep.Deconvolve(recorded, reference);
        return ir.Length <= irLen ? ir : ir[..irLen];
    }

    private static (double[] ResponseDb, double[] SmoothedDb, double[] TargetDb, RoomMeasureAutoEq.PeqFitResult Fit) Analyze(
        double[] ir, double[] freqs, int fs, RoomMeasureAutoEq.PeqFitConfig fitConfig, MicCalibration? calibration)
    {
        var responseDb = RoomMeasureAnalysis.IrToResponseDb(ir, freqs, fs);
        if (calibration is not null) responseDb = calibration.Correct(responseDb, freqs);
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < freqs.Length; i++)
        {
            if (freqs[i] is < 200 or > 2_000) continue;
            sum += responseDb[i];
            count++;
        }
        var center = count == 0 ? 0 : sum / count;
        for (var i = 0; i < freqs.Length; i++) responseDb[i] -= center;
        var smoothed = RoomMeasureAnalysis.SmoothGaussianLogF(freqs, responseDb, RoomMeasureAnalysis.SmoothingWidthVariable(freqs));
        var level = RoomMeasureAnalysis.AutoTargetLevel(smoothed, freqs);
        var target = RoomMeasureAnalysis.BuildTarget(
            freqs, level, lfCutoffHz: 15, lfSlopeDbOct: 24, hfFallStartHz: 10_000, hfFallDbOct: 1.5);
        var fit = RoomMeasureAutoEq.FitPeq(smoothed, target, freqs, fitConfig, fs);
        return (responseDb, smoothed, target, fit);
    }

    private static Measurement MeasureSharedClock(
        IReadOnlyList<double[]> recordings,
        IReadOnlyList<RoomMeasureSweep.LogSweep> sweeps,
        int fs,
        int irLen,
        double[] freqs,
        RoomMeasureAutoEq.PeqFitConfig fitConfig,
        MicCalibration? calibration)
    {
        var ir = RoomMeasureSweep.Deconvolve(recordings[0], sweeps[0].Sweep);
        if (ir.Length > irLen) ir = ir[..irLen];
        var drift = new DriftCheck(0, RoomMeasureSweep.IrSharpness(ir), true, 0, [], double.NaN);
        var (responseDb, smoothedDb, targetDb, fit) = Analyze(ir, freqs, fs, fitConfig, calibration);
        return new Measurement(fs, ir, responseDb, smoothedDb, freqs, targetDb, fit, drift, true, null);
    }
}
