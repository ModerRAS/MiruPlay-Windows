namespace MiruPlay.Windows.Services;

/// <summary>
/// Minimal radix-2 complex FFT for room-measurement deconvolution.
/// Pure math, no platform dependency.
/// </summary>
internal static class RoomMeasureFft
{
    public static int NextPow2(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static void Transform(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j |= bit;
            if (i >= j) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2.0 * Math.PI / len * (inverse ? 1.0 : -1.0);
            var stepRe = Math.Cos(ang);
            var stepIm = Math.Sin(ang);
            var half = len >> 1;
            for (var start = 0; start < n; start += len)
            {
                var curRe = 1.0;
                var curIm = 0.0;
                for (var k = 0; k < half; k++)
                {
                    var i0 = start + k;
                    var i1 = i0 + half;
                    var uRe = re[i0];
                    var uIm = im[i0];
                    var vRe = re[i1] * curRe - im[i1] * curIm;
                    var vIm = re[i1] * curIm + im[i1] * curRe;
                    re[i0] = uRe + vRe;
                    im[i0] = uIm + vIm;
                    re[i1] = uRe - vRe;
                    im[i1] = uIm - vIm;
                    var nextRe = curRe * stepRe - curIm * stepIm;
                    curIm = curRe * stepIm + curIm * stepRe;
                    curRe = nextRe;
                }
            }
        }
        if (!inverse) return;
        for (var i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }

    public static (double[] Re, double[] Im) Rfft(double[] x, int n)
    {
        var re = new double[n];
        var im = new double[n];
        Array.Copy(x, re, Math.Min(x.Length, n));
        Transform(re, im, inverse: false);
        var m = n / 2 + 1;
        return (re[..m], im[..m]);
    }

    public static double[] Irfft(double[] hRe, double[] hIm, int n)
    {
        var re = new double[n];
        var im = new double[n];
        var m = n / 2 + 1;
        Array.Copy(hRe, re, Math.Min(hRe.Length, m));
        Array.Copy(hIm, im, Math.Min(hIm.Length, m));
        for (var i = m; i < n; i++)
        {
            var k = n - i;
            re[i] = hRe[k];
            im[i] = -hIm[k];
        }
        Transform(re, im, inverse: true);
        return re;
    }

    public static double[] FftConvolve(double[] a, double[] b)
    {
        if (a.Length == 0 || b.Length == 0) return [];
        var outLen = a.Length + b.Length - 1;
        var n = NextPow2(outLen);
        var m = n / 2 + 1;
        var (aRe, aIm) = Rfft(a, n);
        var (bRe, bIm) = Rfft(b, n);
        var hRe = new double[m];
        var hIm = new double[m];
        for (var i = 0; i < m; i++)
        {
            hRe[i] = aRe[i] * bRe[i] - aIm[i] * bIm[i];
            hIm[i] = aRe[i] * bIm[i] + aIm[i] * bRe[i];
        }
        return Irfft(hRe, hIm, n)[..outLen];
    }
}
