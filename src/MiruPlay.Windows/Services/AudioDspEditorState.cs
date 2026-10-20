using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public static class AudioDspEditorState
{
    public static AudioDspConfig ReplaceChannelBands(
        AudioDspConfig config,
        string presetId,
        AudioDspChannelTarget target,
        IReadOnlyList<AudioDspBand> bands)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        ArgumentNullException.ThrowIfNull(bands);

        var normalized = config.Normalize();
        var presets = normalized.Presets ?? throw new InvalidOperationException(AppStrings.Get("dsp.emptyPresets"));
        var presetIndex = presets.ToList().FindIndex(item =>
            item.Id.Equals(presetId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (presetIndex < 0) throw new KeyNotFoundException(AppStrings.Get("dsp.presetMissing", presetId));

        var preset = presets[presetIndex];
        var rules = (preset.Rules ?? []).ToList();
        var ruleIndex = rules.FindIndex(rule => rule.Target == target);
        var replacement = new AudioDspChannelRule(
            target,
            bands.Take(AudioDspChannelRule.MaxBands).Select(band => band.Normalize()).ToList(),
            ruleIndex >= 0 ? rules[ruleIndex].OutputGainDb : 0);
        if (ruleIndex >= 0) rules[ruleIndex] = replacement;
        else rules.Add(replacement);

        var updatedPresets = presets.ToList();
        updatedPresets[presetIndex] = preset with { Rules = rules };
        return (normalized with { Presets = updatedPresets }).Normalize();
    }
}
