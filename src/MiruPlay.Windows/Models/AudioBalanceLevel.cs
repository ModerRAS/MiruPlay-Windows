namespace MiruPlay.Windows.Models;

/// <summary>
/// mpv-side loudness balance (AGC) strength. Storage values are the lowercase
/// Android contract values (off/low/medium/high); unknown values normalise to OFF.
/// </summary>
public enum AudioBalanceLevel
{
    Off,
    Low,
    Medium,
    High,
}

public static class AudioBalanceLevels
{
    public static IReadOnlyList<string> StorageValues { get; } = ["off", "low", "medium", "high"];

    public static string ToStorageValue(AudioBalanceLevel level) => level switch
    {
        AudioBalanceLevel.Low => "low",
        AudioBalanceLevel.Medium => "medium",
        AudioBalanceLevel.High => "high",
        _ => "off",
    };

    public static AudioBalanceLevel FromStorageValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => AudioBalanceLevel.Low,
        "medium" => AudioBalanceLevel.Medium,
        "high" => AudioBalanceLevel.High,
        _ => AudioBalanceLevel.Off,
    };

    public static bool IsEnabled(this AudioBalanceLevel level) => level != AudioBalanceLevel.Off;
}
