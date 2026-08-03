using System.Globalization;
using System.Text;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record AudioDspFilterGraph(
    string AfValue,
    IReadOnlyList<string> MpvArguments,
    string EffectiveRoute,
    IReadOnlyList<string> Warnings);

public static class AudioDspFilterGraphCompiler
{
    private const int GraphSampleCount = 65;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static AudioDspFilterGraph Compile(
        AudioDspConfig config,
        AudioDspChannelLayout layout,
        int sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var normalized = config.Normalize();
        var errors = normalized.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException($"音频 DSP 配置无效: {string.Join("; ", errors)}");
        if (!normalized.Enabled)
            return new AudioDspFilterGraph("", [], "disabled", []);

        var preset = normalized.Presets!
            .First(item => item.Id.Equals(normalized.SelectedPresetId, StringComparison.OrdinalIgnoreCase));
        var warnings = CollectWarnings(preset, layout);
        var responses = AudioDspSignalMath.SampleChannels(preset, layout, sampleRateHz);
        var filters = new List<string>();
        if (Math.Abs(preset.PreampDb) > 1e-9)
            filters.Add($"volume={Format(preset.PreampDb)}dB");

        filters.Add(BuildPerChannelGraph(preset, layout, sampleRateHz, responses));
        AppendOutputRoute(filters, preset.OutputMode, layout);
        if (preset.Limiter is { Enabled: true } limiter)
        {
            var limit = Math.Pow(10, limiter.CeilingDb / 20);
            filters.Add($"alimiter=limit={Format(limit)}:release={Format(limiter.ReleaseMs)}");
        }

        var afValue = string.Join(',', filters.Where(filter => filter.Length > 0));
        if (afValue.Length == 0) afValue = "anull";
        var arguments = new List<string>
        {
            "--audio-format=float",
            "--audio-spdif=no",
            "--audio-exclusive=no",
            "--audio-channels=auto",
            $"--af=lavfi=[{afValue}]",
        };
        return new AudioDspFilterGraph(
            afValue,
            arguments,
            AudioDspSignalMath.ResolveOutputRoute(preset.OutputMode),
            warnings);
    }

    private static string BuildPerChannelGraph(
        AudioDspPreset preset,
        AudioDspChannelLayout layout,
        int sampleRateHz,
        IReadOnlyList<AudioDspChannelResponse> responses)
    {
        if (layout.Channels.Count == 0) return "anull";

        var splitOutputs = string.Concat(layout.Channels.Select(channel => $"[{channel}]"));
        var split = $"channelsplit=channel_layout={Escape(layout.Id)}{splitOutputs}";
        var branches = new StringBuilder(split);
        var linear = preset.PhaseMode == AudioDspPhaseMode.Linear;
        var taps = (int)preset.FirQuality;
        var delaySeconds = (taps - 1) / (2d * sampleRateHz);
        var accuracy = preset.FirQuality switch
        {
            AudioDspFirQuality.Low => 5,
            AudioDspFirQuality.High => 15,
            _ => 10,
        };

        for (var index = 0; index < layout.Channels.Count; index++)
        {
            var channel = layout.Channels[index];
            var response = responses[index];
            branches.Append(';').Append('[').Append(channel).Append(']');
            branches.Append("firequalizer=");
            branches.Append("gain_entry=").Append('\'').Append(BuildGainEntries(response, sampleRateHz)).Append('\'');
            branches.Append(":fixed=true");
            branches.Append(":zero_phase=false");
            branches.Append(":accuracy=").Append(accuracy.ToString(Invariant));
            branches.Append(":wfunc=3");
            if (linear)
            {
                branches.Append(":delay=").Append(Format(delaySeconds));
            }
            else
            {
                branches.Append(":delay=0:min_phase=true");
            }
            branches.Append('[').Append(channel).Append("_dsp]");
        }

        branches.Append(';');
        branches.Append(string.Concat(layout.Channels.Select(channel => $"[{channel}_dsp]")));
        branches.Append("amerge=inputs=").Append(layout.Channels.Count.ToString(Invariant));
        return branches.ToString();
    }

    private static string BuildGainEntries(AudioDspChannelResponse response, int sampleRateHz)
    {
        var maxFrequency = sampleRateHz / 2d;
        var builder = new StringBuilder();
        for (var index = 0; index < GraphSampleCount; index++)
        {
            var frequency = index == 0
                ? 0
                : 10 * Math.Pow(maxFrequency / 10, (double)(index - 1) / (GraphSampleCount - 2));
            if (index > 0) builder.Append(';');
            builder.Append("entry(")
                .Append(Format(frequency))
                .Append(',')
                .Append(Format(Math.Clamp(response.MagnitudeDbAt(frequency), -120, 24)))
                .Append(')');
        }
        return builder.ToString();
    }

    private static void AppendOutputRoute(
        List<string> filters,
        AudioDspOutputMode mode,
        AudioDspChannelLayout layout)
    {
        switch (mode)
        {
            case AudioDspOutputMode.StereoDownmix:
                filters.Add(BuildPanDownmix(layout));
                break;
            case AudioDspOutputMode.HrtfBinaural:
                filters.Add("headphone=map=FL\\|FR\\|FC\\|BL\\|BR");
                break;
        }
    }

    private static string BuildPanDownmix(AudioDspChannelLayout layout)
    {
        var matrix = AudioDspSignalMath.BuildStereoDownmixMatrix(layout);
        var left = BuildPanExpression(layout, matrix, 0);
        var right = BuildPanExpression(layout, matrix, 1);
        return $"pan=stereo|FL={left}|FR={right}";
    }

    private static string BuildPanExpression(AudioDspChannelLayout layout, double[,] matrix, int output)
    {
        var terms = new List<string>();
        for (var index = 0; index < layout.Channels.Count; index++)
        {
            var coefficient = matrix[output, index];
            if (Math.Abs(coefficient) < 1e-12) continue;
            var channel = layout.Channels[index];
            var value = Format(Math.Abs(coefficient));
            var term = Math.Abs(coefficient - 1) < 1e-12 ? channel : $"{value}*{channel}";
            if (coefficient < 0) term = $"-{term}";
            terms.Add(term);
        }
        return terms.Count == 0 ? "0" : string.Join('+', terms);
    }

    private static List<string> CollectWarnings(
        AudioDspPreset preset,
        AudioDspChannelLayout layout)
    {
        var warnings = new List<string>();
        foreach (var rule in preset.Rules ?? [])
        {
            if (rule.Target != AudioDspChannelTarget.All &&
                AudioDspSignalMath.ResolveTarget(rule.Target, layout).Count == 0)
            {
                warnings.Add($"布局 {layout.Id} 不包含目标声道 {rule.Target}，该规则已忽略。");
            }
        }
        if (preset.OutputMode == AudioDspOutputMode.HrtfBinaural)
            warnings.Add("HRTF 使用 FFmpeg headphone 默认 HRIR；未提供外部 HRIR 文件。");
        return warnings;
    }

    private static string Format(double value) =>
        value.ToString("0.######", Invariant);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
}
