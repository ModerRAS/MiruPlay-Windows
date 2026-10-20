using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

/// <summary>
/// REW "Match Response to Target" auto-EQ and measured-preset factory.
/// Cut-only by default; bands flow into the playback DSP through the shared
/// biquad path with no coefficient translation. Invalid measurements never
/// produce a preset (fail-closed gate).
/// </summary>
public static class RoomMeasureAutoEq
{
    public sealed record PeqFitConfig(
        double MatchLoHz = 20,
        double MatchHiHz = 300,
        double FlatnessTargetDb = 1,
        int MaxFilters = 10,
        double IndividualMaxBoostDb = 0,
        double OverallMaxBoostDb = 0,
        bool AllowNarrowBelow200 = true,
        bool VaryMaxQAbove200 = true,
        double QMin = 0.5,
        bool DropSmallFilters = true,
        bool TrimToCrossings = true,
        IReadOnlyList<double>? QGrid = null)
    {
        public IReadOnlyList<double> SafeQGrid { get; } =
            QGrid ?? [0.7, 1.0, 1.4, 2.0, 2.8, 4.0, 5.6, 8.0, 11.0];
    }

    public sealed record PeqFitResult(
        IReadOnlyList<AudioDspBand> Bands,
        double MatchLoHz,
        double MatchHiHz,
        double MaxDeviationDb,
        double RmsDeviationDb,
        double OverallMaxBoostDb);

    /// <summary>60 dB decay time of a second-order resonator, in seconds.</summary>
    public static double T60OfFilter(double f0Hz, double q) => 2.1986 * q / f0Hz;

    public static double MaxQAt(double fHz, bool isBoost, PeqFitConfig cfg)
    {
        double qCap;
        if (fHz < 200)
            qCap = cfg.AllowNarrowBelow200 ? 10 : cfg.VaryMaxQAbove200 ? 10 : 5;
        else if (cfg.VaryMaxQAbove200)
            qCap = 10 * Math.Pow(3.0 / 10, Math.Min(Math.Log2(fHz / 200) / Math.Log2(10_000.0 / 200), 1));
        else
            qCap = 5;
        if (isBoost)
        {
            double boostCap;
            if (cfg.VaryMaxQAbove200 && fHz >= 200)
            {
                var t = Math.Min(Math.Log2(fHz / 200) / Math.Log2(10_000.0 / 200), 1);
                boostCap = 7.5 * Math.Pow(3.0 / 7.5, t);
            }
            else
            {
                boostCap = Math.Min(7.5, fHz / 6.22);
            }
            qCap = Math.Min(qCap, boostCap);
        }
        return Math.Max(qCap, cfg.QMin);
    }

    public static double[] BandResponseDb(AudioDspBand band, double[] freqs, int fs) =>
        freqs.Select(f => AudioDspSignalMath.BandMagnitudeDb(band, f, fs)).ToArray();

    public static (double Lo, double Hi) TrimMatchRange(
        double[] measuredDb, double[] targetDb, double[] freqs, PeqFitConfig cfg)
    {
        if (!cfg.TrimToCrossings) return (cfg.MatchLoHz, cfg.MatchHiHz);
        var firstAbove = -1;
        var lastBelow = -1;
        for (var i = 0; i < freqs.Length; i++)
        {
            var f = freqs[i];
            if (f < cfg.MatchLoHz || f > cfg.MatchHiHz) continue;
            if (measuredDb[i] - targetDb[i] > 0)
            {
                if (firstAbove < 0) firstAbove = i;
                lastBelow = i;
            }
        }
        return firstAbove < 0 ? (cfg.MatchLoHz, cfg.MatchHiHz) : (freqs[firstAbove], freqs[lastBelow]);
    }

    public static PeqFitResult FitPeq(
        double[] measuredDb, double[] targetDb, double[] freqs, PeqFitConfig cfg, int fs)
    {
        var (lo, hi) = TrimMatchRange(measuredDb, targetDb, freqs, cfg);
        var inRange = freqs.Select(f => f >= lo && f <= hi).ToArray();
        var rangeCount = inRange.Count(v => v);
        if (rangeCount == 0) return new PeqFitResult([], lo, hi, 0, 0, 0);

        var correction = new double[freqs.Length];
        var bands = new List<AudioDspBand>();
        var active = (bool[])inRange.Clone();

        while (bands.Count < cfg.MaxFilters && active.Contains(true))
        {
            var rmsBefore = Rms(freqs, inRange, rangeCount, i => measuredDb[i] + correction[i] - targetDb[i]);
            var worst = -1;
            var worstAbs = 0.0;
            var worstDb = 0.0;
            for (var i = 0; i < freqs.Length; i++)
            {
                if (!active[i]) continue;
                var err = measuredDb[i] + correction[i] - targetDb[i];
                if (Math.Abs(err) > worstAbs)
                {
                    worstAbs = Math.Abs(err);
                    worstDb = err;
                    worst = i;
                }
            }
            if (worst < 0 || worstAbs < cfg.FlatnessTargetDb) break;

            var fSeed = freqs[worst];
            var gainSeed = ClampGain(-worstDb, cfg);
            // Boost needed but cut-only constraint forbids it → deactivate and move on.
            if (worstDb < 0 && gainSeed >= -1e-9)
            {
                active[worst] = false;
                continue;
            }

            var qCap = MaxQAt(fSeed, gainSeed > 0, cfg);
            var qCandidates = cfg.SafeQGrid.Where(q => q >= cfg.QMin && q <= qCap).ToList();
            if (qCandidates.Count == 0) qCandidates = [Math.Max(cfg.QMin, Math.Min(1, qCap))];

            var bestRms = double.MaxValue;
            AudioDspBand? bestBand = null;
            double[]? bestResp = null;
            foreach (var q0 in qCandidates)
            {
                var band = new AudioDspBand(AudioDspFilterType.Peaking, fSeed, gainSeed, q0);
                var resp = BandResponseDb(band, freqs, fs);
                var rms = Rms(freqs, inRange, rangeCount, i => measuredDb[i] + correction[i] - targetDb[i] + resp[i]);
                if (rms < bestRms)
                {
                    bestRms = rms;
                    bestBand = band;
                    bestResp = resp;
                }
            }

            if (bestRms > rmsBefore - 0.02)
            {
                active[worst] = false;
                continue;
            }

            bands.Add(bestBand!);
            for (var i = 0; i < freqs.Length; i++) correction[i] += bestResp![i];
            active[worst] = false;
        }

        var kept = cfg.DropSmallFilters
            ? bands.Where(band => Math.Abs(band.GainDb) >= cfg.FlatnessTargetDb / 2).ToList()
            : bands;

        var total = freqs.Select((_, i) => measuredDb[i] - targetDb[i]).ToArray();
        foreach (var band in kept)
        {
            var resp = BandResponseDb(band, freqs, fs);
            for (var i = 0; i < freqs.Length; i++) total[i] += resp[i];
        }
        var maxDev = 0.0;
        var rmsSq = 0.0;
        for (var i = 0; i < freqs.Length; i++)
        {
            if (!inRange[i]) continue;
            maxDev = Math.Max(maxDev, Math.Abs(total[i]));
            rmsSq += total[i] * total[i];
        }
        return new PeqFitResult(
            kept, lo, hi, maxDev, Math.Sqrt(rmsSq / rangeCount),
            kept.Count == 0 ? 0 : kept.Max(band => band.GainDb));
    }

    private static double Rms(double[] freqs, bool[] inRange, int rangeCount, Func<int, double> error)
    {
        var sq = 0.0;
        for (var i = 0; i < freqs.Length; i++)
            if (inRange[i]) sq += error(i) * error(i);
        return Math.Sqrt(sq / rangeCount);
    }

    private static double ClampGain(double gain, PeqFitConfig cfg)
    {
        var hi = Math.Max(0, cfg.IndividualMaxBoostDb);
        if (gain > 0) gain = Math.Min(gain, hi);
        return Math.Clamp(gain, -24, hi);
    }

    public sealed record MeasuredBands(
        IReadOnlyList<AudioDspBand> Bands,
        bool Valid,
        string? InvalidReason = null);

    public const string PresetIdPrefix = "measured-";

    public static AudioDspPreset BuildPreset(
        MeasuredBands measured,
        long timestampMs,
        AudioDspChannelTarget target = AudioDspChannelTarget.All,
        string? presetName = null)
    {
        ArgumentNullException.ThrowIfNull(measured);
        if (!measured.Valid) throw new ArgumentException(measured.InvalidReason ?? "measurement is invalid");
        var rule = new AudioDspChannelRule(target, measured.Bands, 0); // Cut-only fit: no headroom correction.
        return new AudioDspPreset(
            $"{PresetIdPrefix}{timestampMs}",
            presetName ?? $"房间校准 {DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).ToLocalTime():MM-dd HH:mm}",
            Rules: [rule]).Normalize();
    }

    public static AudioDspConfig ApplyToConfig(
        AudioDspConfig config,
        MeasuredBands measured,
        long timestampMs,
        AudioDspChannelTarget target = AudioDspChannelTarget.All,
        string? presetName = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var preset = BuildPreset(measured, timestampMs, target, presetName);
        var normalized = config.Normalize();
        return normalized with
        {
            Presets = normalized.Presets!.Where(item => item.Id != preset.Id).Append(preset).ToList(),
            SelectedPresetId = preset.Id,
        };
    }
}
