namespace MiruPlay.Windows.Services;

/// <summary>
/// Frequency-response analysis: log grid, IR→response, REW variable smoothing,
/// target curve. Ports the REW behaviours used by the Android measurement core.
/// </summary>
public static class RoomMeasureAnalysis
{
    public static double[] LogFreqGrid(double fLo, double fHi, int perOctave)
    {
        var octaves = Math.Log2(fHi / fLo);
        var n = (int)(octaves * perOctave) + 1;
        return Enumerable.Range(0, n).Select(i => fLo * Math.Pow(2, (double)i / perOctave)).ToArray();
    }

    public static double[] IrToResponseDb(double[] ir, double[] freqs, int fs)
    {
        var nFft = RoomMeasureFft.NextPow2(ir.Length);
        var m = nFft / 2 + 1;
        var (hRe, hIm) = RoomMeasureFft.Rfft(ir, nFft);
        var magDb = new double[m];
        for (var i = 0; i < m; i++)
        {
            var mag = Math.Sqrt(hRe[i] * hRe[i] + hIm[i] * hIm[i]);
            magDb[i] = 20 * Math.Log10(Math.Max(mag, 1e-12));
        }
        var fAxis = Enumerable.Range(0, m).Select(i => (double)i * fs / nFft).ToArray();
        return freqs.Select(f => Interp(f, fAxis, magDb)).ToArray();
    }

    /// <summary>REW variable smoothing: 1/48 oct below 100 Hz, 1/3 oct above 10 kHz.</summary>
    public static double[] SmoothingWidthVariable(double[] freqs) => freqs.Select(f =>
        f <= 100 ? 1.0 / 48 :
        f >= 10_000 ? 1.0 / 3 :
        f <= 1_000 ? (1.0 / 48) * Math.Pow(8, Math.Log2(f / 100) / Math.Log2(10)) :
        (1.0 / 6) * Math.Pow(2, Math.Log2(f / 1_000) / Math.Log2(10))).ToArray();

    /// <summary>Gaussian smoothing on the log-frequency axis; widths are FWHM in octaves.</summary>
    public static double[] SmoothGaussianLogF(double[] freqs, double[] db, double[] widthOctaves)
    {
        var n = freqs.Length;
        var lf = freqs.Select(f => Math.Log2(f)).ToArray();
        var output = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sigma = widthOctaves[i] / 2.3548;
            var acc = 0.0;
            var wsum = 0.0;
            for (var k = 0; k < n; k++)
            {
                var d = (lf[k] - lf[i]) / sigma;
                if (Math.Abs(d) > 4) continue;
                var weight = Math.Exp(-0.5 * d * d);
                acc += weight * db[k];
                wsum += weight;
            }
            output[i] = wsum <= 0 ? db[i] : acc / wsum;
        }
        return output;
    }

    public static double[] BuildTarget(
        double[] freqs,
        double targetLevelDb,
        double lfCutoffHz = 40,
        double lfSlopeDbOct = 24,
        double hfFallStartHz = 10_000,
        double hfFallDbOct = 0,
        double lfRiseStartHz = 200,
        double lfRiseEndHz = 50,
        double lfRiseDb = 0)
    {
        var target = freqs.Select(_ => targetLevelDb).ToArray();
        for (var i = 0; i < freqs.Length; i++)
        {
            var f = freqs[i];
            if (lfCutoffHz > 0 && f < lfCutoffHz)
                target[i] -= lfSlopeDbOct * Math.Log2(lfCutoffHz / Math.Max(f, 1e-6));
            if (hfFallDbOct > 0 && f > hfFallStartHz)
                target[i] -= hfFallDbOct * Math.Log2(f / hfFallStartHz);
        }
        if (lfRiseDb > 0 && lfRiseStartHz > lfRiseEndHz)
        {
            var denom = Math.Log2(lfRiseStartHz / lfRiseEndHz);
            for (var i = 0; i < freqs.Length; i++)
            {
                var f = freqs[i];
                if (f < lfRiseStartHz)
                    target[i] += lfRiseDb * Math.Clamp(Math.Log2(lfRiseStartHz / Math.Max(f, 1e-6)) / denom, 0, 1);
            }
        }
        return target;
    }

    /// <summary>REW "Calculate target level from response": 200 Hz–2 kHz mean.</summary>
    public static double AutoTargetLevel(double[] measuredDb, double[] freqs, double loHz = 200, double hiHz = 2_000)
    {
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < freqs.Length; i++)
        {
            if (freqs[i] < loHz || freqs[i] > hiHz) continue;
            sum += measuredDb[i];
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    private static double Interp(double x, double[] xs, double[] ys)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var lo = 0;
        var hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (xs[mid] <= x) lo = mid;
            else hi = mid;
        }
        var frac = (x - xs[lo]) / (xs[hi] - xs[lo]);
        return ys[lo] * (1.0 - frac) + ys[hi] * frac;
    }
}
