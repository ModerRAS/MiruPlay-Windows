using System.Collections.Concurrent;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Farina logarithmic sine sweep, regularized deconvolution, drift simulation
/// and dual-sweep quality gates. The inverse envelope is proportional to the
/// instantaneous frequency (a √f envelope would leave a 20 dB in-band tilt),
/// and division by the sweep spectrum is regularized because the sweep has
/// almost no energy outside [f1, f2].
/// </summary>
public static class RoomMeasureSweep
{
    public sealed record LogSweep(double[] Sweep, double[] Inverse);

    public static LogSweep Generate(double f1, double f2, double durationS, int fs)
    {
        var n = (int)(durationS * fs);
        var l = Math.Log(2 * Math.PI * f2 / (2 * Math.PI * f1));
        var sweep = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / fs;
            sweep[i] = Math.Sin(2 * Math.PI * f1 * durationS / l * (Math.Exp(t / durationS * l) - 1.0));
        }
        var inverse = new double[n];
        var maxAbs = 0.0;
        for (var i = 0; i < n; i++)
        {
            var tRev = (double)(n - 1 - i) / fs;
            var envelope = Math.Exp(tRev / durationS * l); // ∝ f(t).
            var v = sweep[n - 1 - i] * envelope;
            inverse[i] = v;
            maxAbs = Math.Max(maxAbs, Math.Abs(v));
        }
        for (var i = 0; i < n; i++) inverse[i] /= maxAbs;
        return new LogSweep(sweep, inverse);
    }

    public static double[] Deconvolve(double[] recorded, double[] sweep, double regularization = 1e-6)
    {
        var nFft = RoomMeasureFft.NextPow2(recorded.Length + sweep.Length);
        var (yRe, yIm) = RoomMeasureFft.Rfft(recorded, nFft);
        return Deconvolve(yRe, yIm, nFft, sweep, recorded.Length - sweep.Length + 1, regularization);
    }

    public static double[] Deconvolve(
        double[] yRe, double[] yIm, int nFft, double[] sweep, int outputLength, double regularization = 1e-6)
    {
        var m = nFft / 2 + 1;
        var (xRe, xIm) = RoomMeasureFft.Rfft(sweep, nFft);
        var maxX2 = 0.0;
        for (var i = 0; i < m; i++) maxX2 = Math.Max(maxX2, xRe[i] * xRe[i] + xIm[i] * xIm[i]);
        var eps = regularization * maxX2;
        var hRe = new double[m];
        var hIm = new double[m];
        for (var i = 0; i < m; i++)
        {
            var denom = xRe[i] * xRe[i] + xIm[i] * xIm[i] + eps;
            hRe[i] = (yRe[i] * xRe[i] + yIm[i] * xIm[i]) / denom;
            hIm[i] = (yIm[i] * xRe[i] - yRe[i] * xIm[i]) / denom;
        }
        return RoomMeasureFft.Irfft(hRe, hIm, nFft)[..Math.Min(outputLength, nFft)];
    }

    /// <summary>Simulate a capture clock deviating by ppm via linear resampling.</summary>
    public static double[] ApplyDrift(double[] x, double ppm)
    {
        if (ppm == 0) return (double[])x.Clone();
        var n = x.Length;
        var output = new double[n];
        var scale = 1.0 + ppm * 1e-6;
        for (var i = 0; i < n; i++)
        {
            var index = i * scale;
            var i0 = (int)index;
            var frac = index - i0;
            output[i] = i0 >= n - 1 ? x[n - 1] : x[i0] * (1.0 - frac) + x[i0 + 1] * frac;
        }
        return output;
    }

    /// <summary>Peak concentration max|IR|/rms(IR); drift spreads energy and lowers it.</summary>
    public static double IrSharpness(double[] ir)
    {
        var sumSq = 0.0;
        var maxAbs = 0.0;
        foreach (var v in ir)
        {
            sumSq += v * v;
            maxAbs = Math.Max(maxAbs, Math.Abs(v));
        }
        return maxAbs / (Math.Sqrt(sumSq / ir.Length) + 1e-30);
    }

    public static double CosineSimilarity(double[] a, double[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var dot = 0.0;
        var na = 0.0;
        var nb = 0.0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-30);
    }

    public sealed record DriftEstimate(double EstimatedPpm, double CompensatedSharpness);

    /// <summary>
    /// Coarse 1 ppm grid, fine 0.1 ppm grid, parabolic refinement. Estimates at
    /// ≥ 5 ppm can land on quantization-lattice peaks, so callers must gate
    /// through <see cref="TailSimilarity"/> before applying EQ.
    /// </summary>
    public static DriftEstimate EstimateDrift(
        double[] recorded,
        double[] sweep,
        int irSampleCount,
        double searchRangePpm = 15,
        double coarseStepPpm = 1,
        double fineStepPpm = 0.1)
    {
        var nFft = RoomMeasureFft.NextPow2(recorded.Length + sweep.Length);
        var (yRe, yIm) = RoomMeasureFft.Rfft(recorded, nFft);
        var outLen = recorded.Length - sweep.Length + 1;
        double SharpnessAt(double ppm)
        {
            var reference = ppm == 0 ? sweep : ApplyDrift(sweep, ppm);
            var ir = Deconvolve(yRe, yIm, nFft, reference, outLen);
            return IrSharpness(ir[..Math.Min(irSampleCount, ir.Length)]);
        }

        (double Ppm, double Score) EvalAll(List<double> candidates)
        {
            var scores = new double[candidates.Count];
            Parallel.ForEach(
                Partitioner.Create(0, candidates.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) },
                range =>
                {
                    for (var i = range.Item1; i < range.Item2; i++) scores[i] = SharpnessAt(candidates[i]);
                });
            var best = 0;
            for (var i = 1; i < scores.Length; i++)
                if (scores[i] > scores[best]) best = i;
            return (candidates[best], scores[best]);
        }

        var coarse = new List<double>();
        for (var p = -searchRangePpm; p <= searchRangePpm + 1e-9; p += coarseStepPpm) coarse.Add(p);
        var (coarseBest, _) = EvalAll(coarse);
        var fine = new List<double>();
        for (var p = coarseBest - coarseStepPpm; p <= coarseBest + coarseStepPpm + 1e-9; p += fineStepPpm) fine.Add(p);
        var (fineBest, fineScore) = EvalAll(fine);
        var ppm = RefineParabolic(fineBest, fineScore, fineStepPpm, SharpnessAt);
        return new DriftEstimate(ppm, SharpnessAt(ppm));
    }

    /// <summary>
    /// Cosine similarity of the room tails past each IR's direct peak. Correct
    /// compensation recovers the same tail (~0.9+); wrong compensation
    /// decorrelates (~0.6–0.75).
    /// </summary>
    public static double TailSimilarity(double[] irA, double[] irB, int skipAfterPeak = 60)
    {
        var start = Math.Max(PeakIndex(irA), PeakIndex(irB)) + skipAfterPeak;
        var n = Math.Min(irA.Length, irB.Length);
        if (start >= n) return 0;
        var dot = 0.0;
        var na = 0.0;
        var nb = 0.0;
        for (var i = start; i < n; i++)
        {
            dot += irA[i] * irB[i];
            na += irA[i] * irA[i];
            nb += irB[i] * irB[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-30);
    }

    private static int PeakIndex(double[] ir)
    {
        var best = 0;
        for (var i = 0; i < ir.Length; i++)
            if (Math.Abs(ir[i]) > Math.Abs(ir[best])) best = i;
        return best;
    }

    private static double RefineParabolic(double x0, double y0, double step, Func<double, double> score)
    {
        var yMinus = score(x0 - step);
        var yPlus = score(x0 + step);
        var denom = yMinus - 2.0 * y0 + yPlus;
        if (Math.Abs(denom) < 1e-12) return x0;
        return x0 + Math.Clamp(0.5 * (yMinus - yPlus) / denom * step, -step, step);
    }
}
