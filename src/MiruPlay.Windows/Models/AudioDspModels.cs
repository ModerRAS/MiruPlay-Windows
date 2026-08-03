using System.Globalization;

namespace MiruPlay.Windows.Models;

public enum AudioDspPhaseMode
{
    Minimum,
    Linear,
}

public enum AudioDspOutputMode
{
    AutoPreserve,
    StereoDownmix,
    HrtfBinaural,
}

public enum AudioDspFirQuality
{
    Low = 1024,
    Medium = 2048,
    High = 4096,
}

public enum AudioDspFilterType
{
    Peaking,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
    Notch,
    BandPass,
}

public enum AudioDspChannelTarget
{
    All,
    Front,
    CenterLfe,
    Surround,
    Surround51,
    Surround71,
    Left,
    Right,
    Center,
    Lfe,
    LeftSurround,
    RightSurround,
}

public sealed record AudioDspBand(
    AudioDspFilterType Type = AudioDspFilterType.Peaking,
    double FrequencyHz = 1_000,
    double GainDb = 0,
    double Q = 1,
    bool Enabled = true)
{
    public const double MinFrequencyHz = 10;
    public const double MaxFrequencyHz = 24_000;
    public const double MinGainDb = -24;
    public const double MaxGainDb = 24;
    public const double MinQ = 0.1;
    public const double MaxQ = 20;

    public AudioDspBand Normalize() => this with
    {
        FrequencyHz = NormalizeFinite(FrequencyHz, 1_000, MinFrequencyHz, MaxFrequencyHz),
        GainDb = NormalizeFinite(GainDb, 0, MinGainDb, MaxGainDb),
        Q = NormalizeFinite(Q, 1, MinQ, MaxQ),
        Type = Enum.IsDefined(Type) ? Type : AudioDspFilterType.Peaking,
    };

    private static double NormalizeFinite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record AudioDspChannelRule(
    AudioDspChannelTarget Target = AudioDspChannelTarget.All,
    IReadOnlyList<AudioDspBand>? Bands = null,
    double OutputGainDb = 0)
{
    public const int MaxBands = 32;

    public AudioDspChannelRule Normalize() => this with
    {
        Target = Enum.IsDefined(Target) ? Target : AudioDspChannelTarget.All,
        Bands = (Bands ?? []).Take(MaxBands).Select(band => band.Normalize()).ToList(),
        OutputGainDb = double.IsFinite(OutputGainDb)
            ? Math.Clamp(OutputGainDb, AudioDspBand.MinGainDb, AudioDspBand.MaxGainDb)
            : 0,
    };
}

public sealed record AudioDspLimiter(
    bool Enabled = false,
    double CeilingDb = -1,
    double ReleaseMs = 100)
{
    public AudioDspLimiter Normalize() => this with
    {
        CeilingDb = double.IsFinite(CeilingDb) ? Math.Clamp(CeilingDb, -24, 0) : -1,
        ReleaseMs = double.IsFinite(ReleaseMs) ? Math.Clamp(ReleaseMs, 1, 2_000) : 100,
    };
}

public sealed record AudioDspPreset(
    string Id,
    string Name,
    double PreampDb = 0,
    AudioDspPhaseMode PhaseMode = AudioDspPhaseMode.Minimum,
    AudioDspFirQuality FirQuality = AudioDspFirQuality.Medium,
    AudioDspOutputMode OutputMode = AudioDspOutputMode.AutoPreserve,
    IReadOnlyList<AudioDspChannelRule>? Rules = null,
    AudioDspLimiter? Limiter = null)
{
    public static AudioDspPreset Neutral() => new("neutral", "Neutral");

    public AudioDspPreset Normalize() => this with
    {
        Id = Id?.Trim() ?? string.Empty,
        Name = string.IsNullOrWhiteSpace(Name) ? "Neutral" : Name.Trim(),
        PreampDb = double.IsFinite(PreampDb) ? Math.Clamp(PreampDb, -24, 12) : 0,
        PhaseMode = Enum.IsDefined(PhaseMode) ? PhaseMode : AudioDspPhaseMode.Minimum,
        FirQuality = Enum.IsDefined(FirQuality) ? FirQuality : AudioDspFirQuality.Medium,
        OutputMode = Enum.IsDefined(OutputMode) ? OutputMode : AudioDspOutputMode.AutoPreserve,
        Rules = (Rules ?? []).Select(rule => rule.Normalize()).ToList(),
        Limiter = (Limiter ?? new AudioDspLimiter()).Normalize(),
    };
}

public sealed record AudioDspConfig(
    bool Enabled = false,
    string SelectedPresetId = "neutral",
    IReadOnlyList<AudioDspPreset>? Presets = null,
    int SchemaVersion = 1)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultPresetId = "neutral";

    public static AudioDspConfig Neutral() => new();

    public AudioDspConfig Normalize()
    {
        var normalized = (Presets ?? [])
            .Select(preset => preset.Normalize())
            .Select((preset, index) => string.IsNullOrWhiteSpace(preset.Id)
                ? preset with { Id = index == 0 ? DefaultPresetId : $"preset-{index}" }
                : preset)
            .DistinctBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0) normalized.Add(AudioDspPreset.Neutral());

        var selectedId = normalized.FirstOrDefault(preset =>
                preset.Id.Equals(SelectedPresetId?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? normalized[0].Id;
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            SelectedPresetId = selectedId,
            Presets = normalized,
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add($"schemaVersion must be {CurrentSchemaVersion}");
        if (Presets is null || Presets.Count == 0)
        {
            errors.Add("presets must not be empty");
            return errors;
        }

        if (!Presets.Any(preset => preset.Id.Equals(SelectedPresetId, StringComparison.OrdinalIgnoreCase)))
            errors.Add("selectedPresetId must reference an existing preset");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var presetIndex = 0; presetIndex < Presets.Count; presetIndex++)
        {
            var preset = Presets[presetIndex];
            var id = preset.Id.Trim();
            if (id.Length == 0) errors.Add($"presets[{presetIndex}].id must not be blank");
            if (!ids.Add(id)) errors.Add($"presets[{presetIndex}].id is duplicate");
            AddRangeError(errors, $"presets[{presetIndex}].preampDb", preset.PreampDb, -24, 12);
            if (!Enum.IsDefined(preset.PhaseMode)) errors.Add($"presets[{presetIndex}].phaseMode is invalid");
            if (!Enum.IsDefined(preset.FirQuality)) errors.Add($"presets[{presetIndex}].firQuality is invalid");
            if (!Enum.IsDefined(preset.OutputMode)) errors.Add($"presets[{presetIndex}].outputMode is invalid");
            if (preset.Rules is null) continue;
            for (var ruleIndex = 0; ruleIndex < preset.Rules.Count; ruleIndex++)
            {
                var rule = preset.Rules[ruleIndex];
                if (!Enum.IsDefined(rule.Target)) errors.Add($"presets[{presetIndex}].rules[{ruleIndex}].target is invalid");
                if (rule.Bands is null) continue;
                if (rule.Bands.Count > AudioDspChannelRule.MaxBands)
                    errors.Add($"presets[{presetIndex}].rules[{ruleIndex}] has too many bands");
                AddRangeError(errors, $"presets[{presetIndex}].rules[{ruleIndex}].outputGainDb", rule.OutputGainDb, -24, 24);
                for (var bandIndex = 0; bandIndex < rule.Bands.Count; bandIndex++)
                {
                    var band = rule.Bands[bandIndex];
                    var prefix = $"presets[{presetIndex}].rules[{ruleIndex}].bands[{bandIndex}]";
                    if (!Enum.IsDefined(band.Type)) errors.Add($"{prefix}.type is invalid");
                    AddRangeError(errors, $"{prefix}.frequencyHz", band.FrequencyHz, AudioDspBand.MinFrequencyHz, AudioDspBand.MaxFrequencyHz);
                    AddRangeError(errors, $"{prefix}.gainDb", band.GainDb, AudioDspBand.MinGainDb, AudioDspBand.MaxGainDb);
                    AddRangeError(errors, $"{prefix}.Q", band.Q, AudioDspBand.MinQ, AudioDspBand.MaxQ);
                }
            }

            var limiter = preset.Limiter;
            if (limiter is null) continue;
            AddRangeError(errors, $"presets[{presetIndex}].limiter.ceilingDb", limiter.CeilingDb, -24, 0);
            AddRangeError(errors, $"presets[{presetIndex}].limiter.releaseMs", limiter.ReleaseMs, 1, 2_000);
        }
        return errors;
    }

    private static void AddRangeError(List<string> errors, string field, double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            errors.Add($"{field} is out of range");
    }
}

public static class AudioDspStorage
{
    public static string ToStorageValue(this AudioDspPhaseMode value) => value switch
    {
        AudioDspPhaseMode.Linear => "linear",
        _ => "minimum",
    };

    public static string ToStorageValue(this AudioDspOutputMode value) => value switch
    {
        AudioDspOutputMode.StereoDownmix => "stereo_downmix",
        AudioDspOutputMode.HrtfBinaural => "hrtf_binaural",
        _ => "auto_preserve",
    };

    public static string ToStorageValue(this AudioDspFirQuality value) => value switch
    {
        AudioDspFirQuality.Low => "low",
        AudioDspFirQuality.High => "high",
        _ => "medium",
    };

    public static string ToStorageValue(this AudioDspFilterType value) => value switch
    {
        AudioDspFilterType.LowShelf => "low_shelf",
        AudioDspFilterType.HighShelf => "high_shelf",
        AudioDspFilterType.LowPass => "low_pass",
        AudioDspFilterType.HighPass => "high_pass",
        AudioDspFilterType.Notch => "notch",
        AudioDspFilterType.BandPass => "band_pass",
        _ => "peaking",
    };

    public static string ToStorageValue(this AudioDspChannelTarget value) => value switch
    {
        AudioDspChannelTarget.CenterLfe => "center_lfe",
        AudioDspChannelTarget.LeftSurround => "left_surround",
        AudioDspChannelTarget.RightSurround => "right_surround",
        AudioDspChannelTarget.Surround51 => "surround_5_1",
        AudioDspChannelTarget.Surround71 => "surround_7_1",
        _ => value.ToString().ToLowerInvariant(),
    };

    public static bool TryParseFilterType(string? value, out AudioDspFilterType type)
    {
        type = value?.Trim().ToUpperInvariant() switch
        {
            "PK" or "PEAKING" => AudioDspFilterType.Peaking,
            "LS" or "LOW_SHELF" => AudioDspFilterType.LowShelf,
            "HS" or "HIGH_SHELF" => AudioDspFilterType.HighShelf,
            "LP" or "LOW_PASS" => AudioDspFilterType.LowPass,
            "HP" or "HIGH_PASS" => AudioDspFilterType.HighPass,
            "NO" or "NOTCH" => AudioDspFilterType.Notch,
            "BP" or "BAND_PASS" => AudioDspFilterType.BandPass,
            _ => default,
        };
        return value?.Trim() is { Length: > 0 } && type switch
        {
            AudioDspFilterType.Peaking => value.Trim().Equals("PK", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("PEAKING", StringComparison.OrdinalIgnoreCase),
            AudioDspFilterType.LowShelf => value.Trim().Equals("LS", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("LOW_SHELF", StringComparison.OrdinalIgnoreCase),
            AudioDspFilterType.HighShelf => value.Trim().Equals("HS", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("HIGH_SHELF", StringComparison.OrdinalIgnoreCase),
            AudioDspFilterType.LowPass => value.Trim().Equals("LP", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("LOW_PASS", StringComparison.OrdinalIgnoreCase),
            AudioDspFilterType.HighPass => value.Trim().Equals("HP", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("HIGH_PASS", StringComparison.OrdinalIgnoreCase),
            AudioDspFilterType.Notch => value.Trim().Equals("NO", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("NOTCH", StringComparison.OrdinalIgnoreCase),
            AudioDspFilterType.BandPass => value.Trim().Equals("BP", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("BAND_PASS", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
