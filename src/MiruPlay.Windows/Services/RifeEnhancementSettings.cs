using System.Globalization;

namespace MiruPlay.Windows.Services;

/// <summary>Storage contract for the optional RIFE enhancement pack sub-settings.
/// The parent switch remains <see cref="MpvFrameInterpolationModes.Rife"/> on
/// <c>AppSettings.FrameInterpolationMode</c>; this record only carries RIFE tuning.</summary>
public sealed record RifeEnhancementSettings(
    int Factor = 2,
    string Model = "4.6",
    int GpuThread = 2,
    string ProcessingHeight = "auto")
{
    public const string AutoProcessingHeight = "auto";
    public const string Model46 = "4.6";
    public const string Model425Lite = "4.25-lite";

    public static RifeEnhancementSettings Default { get; } = new();

    public static IReadOnlyList<int> FactorValues { get; } = [2, 3, 4];

    public static IReadOnlyList<string> ModelValues { get; } = [Model46, Model425Lite];

    public static IReadOnlyList<int> GpuThreadValues { get; } = [1, 2, 3, 4];

    public static IReadOnlyList<string> ProcessingHeightValues { get; } =
        [AutoProcessingHeight, "1080", "720", "576", "540", "480", "360"];

    public static RifeEnhancementSettings Normalize(RifeEnhancementSettings? value)
    {
        value ??= Default;
        var model = ModelValues.FirstOrDefault(item =>
            string.Equals(item, value.Model?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Model46;
        var height = ProcessingHeightValues.FirstOrDefault(item =>
            string.Equals(item, value.ProcessingHeight?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? AutoProcessingHeight;
        return new(
            FactorValues.Contains(value.Factor) ? value.Factor : Default.Factor,
            model,
            GpuThreadValues.Contains(value.GpuThread) ? value.GpuThread : Default.GpuThread,
            height);
    }

    public static int ToModelIndex(string? model) =>
        string.Equals(Normalize(new RifeEnhancementSettings(Model: model ?? Model46)).Model, Model425Lite, StringComparison.Ordinal)
            ? 70
            : 23;

    /// <summary>0 means "do not pre-scale"; otherwise the RIFE processing height.</summary>
    public static int ResolveProcessingHeight(string? value, int? recommendedHeight)
    {
        var normalized = Normalize(new RifeEnhancementSettings(ProcessingHeight: value ?? AutoProcessingHeight));
        if (normalized.ProcessingHeight != AutoProcessingHeight)
            return int.Parse(normalized.ProcessingHeight, CultureInfo.InvariantCulture);
        return recommendedHeight is > 0 ? recommendedHeight.Value : 480;
    }
}
