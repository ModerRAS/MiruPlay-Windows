using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Microphone calibration (UMIK-1 / REW-style .cal file): the microphone's own
/// magnitude response in dB. The measured room response is room × mic, so the
/// mic curve is SUBTRACTED before smoothing / target / fitting.
/// Accepts miniDSP 3-column lines (frequency 0° 90°) with incidence column
/// choice, or plain 2-column lines. Bounded to 200k chars / 8192 points.
/// </summary>
public sealed class MicCalibration
{
    public enum Incidence { ZeroDeg, NinetyDeg }

    public const int MaxTextChars = 200_000;
    public const int MaxPoints = 8_192;

    private static readonly Regex Number = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public double[] FrequenciesHz { get; }
    public double[] GainDb { get; }

    public MicCalibration(double[] frequenciesHz, double[] gainDb)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(gainDb);
        if (frequenciesHz.Length < 2) throw new ArgumentException("校准文件至少需要 2 个有效数据点。");
        if (frequenciesHz.Length != gainDb.Length) throw new ArgumentException("校准数据列数不匹配。");
        if (frequenciesHz[0] <= 0) throw new ArgumentException("校准频率必须为正。");
        for (var i = 1; i < frequenciesHz.Length; i++)
        {
            if (frequenciesHz[i] <= frequenciesHz[i - 1])
                throw new ArgumentException("校准频率必须严格递增。");
        }
        FrequenciesHz = frequenciesHz;
        GainDb = gainDb;
    }

    public double[] Correct(double[] responseDb, double[] freqs)
    {
        var output = new double[freqs.Length];
        for (var i = 0; i < freqs.Length; i++) output[i] = responseDb[i] - Interpolate(freqs[i]);
        return output;
    }

    public double CoverageLoHz => FrequenciesHz[0];
    public double CoverageHiHz => FrequenciesHz[^1];

    public string? CoverageWarning() => CoverageHiHz < 5_000
        ? $"校准文件仅覆盖 {CoverageLoHz:0}–{CoverageHiHz:0} Hz，高频段（5 kHz 以上）没有被校准；看起来文件不完整，建议重新下载完整校准"
        : null;

    private double Interpolate(double frequency)
    {
        var last = FrequenciesHz.Length - 1;
        if (frequency <= FrequenciesHz[0]) return GainDb[0];
        if (frequency >= FrequenciesHz[last]) return GainDb[last];
        var lo = 0;
        var hi = last;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (FrequenciesHz[mid] <= frequency) lo = mid;
            else hi = mid;
        }
        var f0 = FrequenciesHz[lo];
        var frac = Math.Clamp((frequency - f0) / (FrequenciesHz[hi] - f0), 0, 1);
        return GainDb[lo] * (1 - frac) + GainDb[hi] * frac;
    }

    public sealed record Parsed(MicCalibration Calibration, int ColumnCount);

    public static Parsed Parse(string text, Incidence incidence = Incidence.ZeroDeg)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextChars) throw new ArgumentException("校准文件过大。");
        var rows = new List<(int Columns, double Freq, double Gain)>();
        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('"') || line.StartsWith("Sens", StringComparison.OrdinalIgnoreCase)) continue;
            var numbers = Number.Matches(line).Select(match =>
                double.TryParse(match.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ? (double?)value : null).ToList();
            if (numbers.Any(value => value is null)) continue;
            var values = numbers.Select(value => value!.Value).ToList();
            if (values.Count is not (2 or 3) || values[0] is < 1e-3 or > 1e6 ||
                values.Skip(1).Any(value => Math.Abs(value) >= 100)) continue;
            var gain = values.Count == 3
                ? (incidence == Incidence.ZeroDeg ? values[1] : values[2])
                : values[1];
            rows.Add((values.Count, values[0], gain));
            if (rows.Count >= MaxPoints) break;
        }
        if (rows.Count < 2)
            throw new ArgumentException("校准文件没有足够的有效数据点（需要频率 + dB 两列，UMIK 为 频率/0°/90° 三列）。");
        var dominant = rows.GroupBy(row => row.Columns).MaxBy(group => group.Count())!.Key;
        var picked = rows.Where(row => row.Columns == dominant).ToList();
        return new Parsed(
            new MicCalibration(
                picked.Select(row => row.Freq).ToArray(),
                picked.Select(row => row.Gain).ToArray()),
            dominant);
    }
}
