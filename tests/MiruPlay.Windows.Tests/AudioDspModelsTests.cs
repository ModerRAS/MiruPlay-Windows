using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Tests;

public sealed class AudioDspModelsTests
{
    [Fact]
    public void NormalizeCreatesNeutralPresetWhenInputIsEmpty()
    {
        var normalized = new AudioDspConfig(Presets: []).Normalize();

        Assert.False(normalized.Enabled);
        Assert.Equal(AudioDspConfig.DefaultPresetId, normalized.SelectedPresetId);
        var presets = normalized.Presets ?? [];
        Assert.Single(presets);
        Assert.Empty(presets[0].Rules ?? []);
    }

    [Fact]
    public void ValidateRejectsDuplicatePresetIdsAndOutOfRangeBands()
    {
        var preset = new AudioDspPreset(
            "same",
            "Calibration",
            Rules: [new(AudioDspChannelTarget.Left, [new(FrequencyHz: 5, Q: 99)])]);
        var errors = new AudioDspConfig(
            Enabled: true,
            SelectedPresetId: "same",
            Presets: [preset, preset]).Validate();

        Assert.Contains(errors, error => error.Contains("duplicate", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("frequency", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains('Q'));
    }
}
