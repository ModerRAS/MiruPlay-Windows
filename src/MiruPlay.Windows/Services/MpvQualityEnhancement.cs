using System.Globalization;

namespace MiruPlay.Windows.Services;

public enum MpvUpscaleQualityPreset
{
    Off,
    Low,
    Medium,
    High,
}

public enum MpvAnime4KPreset
{
    Off,
    A,
    APlusA,
    B,
    C,
}

/// <summary>Frame interpolation mode: Off, built-in mpv frame mixing (MpvBuiltIn) and the
/// optional RIFE enhancement pack (Rife). RIFE is only effective when the enhancement pack is
/// installed and has passed its self-test; otherwise playback degrades with a reason.</summary>
public enum MpvFrameInterpolationMode
{
    Off,
    MpvBuiltIn,
    Rife,
}

public static class MpvFrameInterpolationModes
{
    public const string Off = "off";
    public const string MpvBuiltIn = "mpv";

    /// <summary>RIFE true frame interpolation via the optional enhancement pack.</summary>
    public const string Rife = "rife";

    /// <summary>Values accepted by settings/API/UI. RIFE is always selectable; playback
    /// degrades to plain mpv with an explicit reason when the pack is not ready.</summary>
    public static IReadOnlyList<string> SupportedValues { get; } = [Off, MpvBuiltIn, Rife];

    /// <summary>No values are reserved any more; kept for API schema compatibility.</summary>
    public static IReadOnlyList<string> ReservedValues { get; } = [];

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        MpvBuiltIn or "mpv_builtin" or "builtin" or "smooth" => MpvBuiltIn,
        Rife or "rife_ncnn" or "ncnn" => Rife,
        _ => Off,
    };

    public static MpvFrameInterpolationMode Parse(string? value) => Normalize(value) switch
    {
        MpvBuiltIn => MpvFrameInterpolationMode.MpvBuiltIn,
        Rife => MpvFrameInterpolationMode.Rife,
        _ => MpvFrameInterpolationMode.Off,
    };

    public static string ToStorageValue(MpvFrameInterpolationMode mode) => mode switch
    {
        MpvFrameInterpolationMode.MpvBuiltIn => MpvBuiltIn,
        MpvFrameInterpolationMode.Rife => Rife,
        _ => Off,
    };
}

/// <summary>Storage-value contract for the optional GPU quality presets shared by
/// settings, WebControl, WPF and TV mode.</summary>
public static class MpvQualityPresetSettings
{
    public const string UpscaleOff = "off";
    public const string UpscaleLow = "low";
    public const string UpscaleMedium = "medium";
    public const string UpscaleHigh = "high";

    public const string Anime4KOff = "off";
    public const string Anime4KA = "a";
    public const string Anime4KAPlusA = "a_plus_a";
    public const string Anime4KB = "b";
    public const string Anime4KC = "c";

    public static IReadOnlyList<string> UpscaleValues { get; } =
        [UpscaleOff, UpscaleLow, UpscaleMedium, UpscaleHigh];

    public static IReadOnlyList<string> Anime4KValues { get; } =
        [Anime4KOff, Anime4KA, Anime4KAPlusA, Anime4KB, Anime4KC];

    public static string NormalizeUpscale(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        UpscaleLow => UpscaleLow,
        UpscaleMedium => UpscaleMedium,
        UpscaleHigh => UpscaleHigh,
        _ => UpscaleOff,
    };

    public static string NormalizeAnime4K(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Anime4KA => Anime4KA,
        Anime4KAPlusA or "a+a" or "aplusa" => Anime4KAPlusA,
        Anime4KB => Anime4KB,
        Anime4KC => Anime4KC,
        _ => Anime4KOff,
    };

    public static MpvUpscaleQualityPreset ParseUpscale(string? value) => NormalizeUpscale(value) switch
    {
        UpscaleLow => MpvUpscaleQualityPreset.Low,
        UpscaleMedium => MpvUpscaleQualityPreset.Medium,
        UpscaleHigh => MpvUpscaleQualityPreset.High,
        _ => MpvUpscaleQualityPreset.Off,
    };

    public static MpvAnime4KPreset ParseAnime4K(string? value) => NormalizeAnime4K(value) switch
    {
        Anime4KA => MpvAnime4KPreset.A,
        Anime4KAPlusA => MpvAnime4KPreset.APlusA,
        Anime4KB => MpvAnime4KPreset.B,
        Anime4KC => MpvAnime4KPreset.C,
        _ => MpvAnime4KPreset.Off,
    };

    public static string ToStorageValue(MpvUpscaleQualityPreset preset) => preset switch
    {
        MpvUpscaleQualityPreset.Low => UpscaleLow,
        MpvUpscaleQualityPreset.Medium => UpscaleMedium,
        MpvUpscaleQualityPreset.High => UpscaleHigh,
        _ => UpscaleOff,
    };

    public static string ToStorageValue(MpvAnime4KPreset preset) => preset switch
    {
        MpvAnime4KPreset.A => Anime4KA,
        MpvAnime4KPreset.APlusA => Anime4KAPlusA,
        MpvAnime4KPreset.B => Anime4KB,
        MpvAnime4KPreset.C => Anime4KC,
        _ => Anime4KOff,
    };
}

/// <summary>Optional GPU quality enhancement request. All members default to the
/// existing mpv behaviour so an empty instance never changes playback output.</summary>
public sealed record MpvQualityEnhancementOptions(
    MpvUpscaleQualityPreset Upscale = MpvUpscaleQualityPreset.Off,
    MpvAnime4KPreset Anime4K = MpvAnime4KPreset.Off,
    MpvFrameInterpolationMode Interpolation = MpvFrameInterpolationMode.Off,
    string? ShaderDirectory = null)
{
    public bool IsEnabled =>
        Upscale != MpvUpscaleQualityPreset.Off ||
        Anime4K != MpvAnime4KPreset.Off ||
        Interpolation != MpvFrameInterpolationMode.Off;
}

public static class MpvQualityEnhancementPaths
{
    public const string ShaderSubdirectory = "shaders/anime4k";

    public static string ResolveShaderDirectory(string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "runtime", "shaders", "anime4k");
}

public enum MpvQualityShaderDecision
{
    NotRequested,
    NotReady,
    Apply,
    SkipHdr,
    SkipAssets,
}

public static class MpvQualityShaderStates
{
    public const string NotRequested = "not_requested";
    public const string NotReady = "not_ready";
    public const string Applied = "applied";
    public const string SkippedHdr = "skipped_hdr";
    public const string SkippedAssets = "skipped_assets";
    public const string Failed = "failed";

    public static string FromDecision(MpvQualityShaderDecision decision) => decision switch
    {
        MpvQualityShaderDecision.Apply => Applied,
        MpvQualityShaderDecision.SkipHdr => SkippedHdr,
        MpvQualityShaderDecision.SkipAssets => SkippedAssets,
        MpvQualityShaderDecision.NotReady => NotReady,
        _ => NotRequested,
    };
}

public static class MpvQualityDegradedReasons
{
    public const string HdrContent = "quality.reason.hdr";
    public const string AssetsMissing = "quality.reason.assetsMissing";
    public const string ShaderFailed = "quality.reason.shaderFailed";
}

public sealed record MpvQualityShaderPlan(
    MpvQualityShaderDecision Decision,
    IReadOnlyList<string> ShaderPaths,
    string? DegradedReasonKey = null,
    IReadOnlyList<string>? MissingFiles = null);

/// <summary>Effective quality state of a running playback session, exposed through
/// the diagnostics API and the WebControl playback status.</summary>
public sealed record MpvQualityEnhancementStatus(
    string UpscalePreset,
    string Anime4KPreset,
    string FrameInterpolationMode,
    string ShaderState,
    string? DegradedReasonKey,
    IReadOnlyList<string> AppliedShaderFiles,
    string? RifeState = null,
    string? RifeDegradedReasonKey = null,
    int? RifeFactor = null,
    string? RifeProcessingHeight = null);

/// <summary>Anime4K v4.0.1 official mpv shader files (MIT). The first version ships
/// the official "Fast/Low-end" chains only; the high-quality VL tier is a later step.</summary>
public static class MpvAnime4KShaderCatalog
{
    public const string Version = "4.0.1";
    public const string ArchiveFileName = "Anime4K_v4.0.zip";
    public const string ArchiveSha256 = "139cd282086457c5adc79caf7b75b8b825091d71c9b54958c18745fea62d7ed7";

    public const string ClampHighlights = "Anime4K_Clamp_Highlights.glsl";
    public const string RestoreCnnM = "Anime4K_Restore_CNN_M.glsl";
    public const string RestoreCnnS = "Anime4K_Restore_CNN_S.glsl";
    public const string RestoreCnnSoftM = "Anime4K_Restore_CNN_Soft_M.glsl";
    public const string UpscaleCnnX2M = "Anime4K_Upscale_CNN_x2_M.glsl";
    public const string UpscaleCnnX2S = "Anime4K_Upscale_CNN_x2_S.glsl";
    public const string UpscaleDenoiseCnnX2M = "Anime4K_Upscale_Denoise_CNN_x2_M.glsl";
    public const string AutoDownscalePreX2 = "Anime4K_AutoDownscalePre_x2.glsl";
    public const string AutoDownscalePreX4 = "Anime4K_AutoDownscalePre_x4.glsl";

    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        ClampHighlights,
        RestoreCnnM,
        RestoreCnnS,
        RestoreCnnSoftM,
        UpscaleCnnX2M,
        UpscaleCnnX2S,
        UpscaleDenoiseCnnX2M,
        AutoDownscalePreX2,
        AutoDownscalePreX4,
    ];

    public static IReadOnlyList<string> FilesFor(MpvAnime4KPreset preset) => preset switch
    {
        MpvAnime4KPreset.A =>
        [
            ClampHighlights,
            RestoreCnnM,
            UpscaleCnnX2M,
            AutoDownscalePreX2,
            AutoDownscalePreX4,
            UpscaleCnnX2S,
        ],
        MpvAnime4KPreset.APlusA =>
        [
            ClampHighlights,
            RestoreCnnM,
            UpscaleCnnX2M,
            RestoreCnnS,
            AutoDownscalePreX2,
            AutoDownscalePreX4,
            UpscaleCnnX2S,
        ],
        MpvAnime4KPreset.B =>
        [
            ClampHighlights,
            RestoreCnnSoftM,
            UpscaleCnnX2M,
            AutoDownscalePreX2,
            AutoDownscalePreX4,
            UpscaleCnnX2S,
        ],
        MpvAnime4KPreset.C =>
        [
            ClampHighlights,
            UpscaleDenoiseCnnX2M,
            AutoDownscalePreX2,
            AutoDownscalePreX4,
            UpscaleCnnX2S,
        ],
        _ => [],
    };

    public static bool TryResolveFiles(
        MpvAnime4KPreset preset,
        string? shaderDirectory,
        out IReadOnlyList<string> paths,
        out IReadOnlyList<string> missingFiles)
    {
        var files = FilesFor(preset);
        if (files.Count == 0)
        {
            paths = [];
            missingFiles = [];
            return true;
        }

        if (string.IsNullOrWhiteSpace(shaderDirectory))
        {
            paths = [];
            missingFiles = files.ToArray();
            return false;
        }

        var missing = new List<string>();
        var resolved = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = Path.Combine(shaderDirectory, file);
            if (!File.Exists(path))
            {
                missing.Add(file);
                continue;
            }
            resolved.Add(path);
        }

        paths = resolved;
        missingFiles = missing;
        return missing.Count == 0;
    }

    public static bool IsAvailable(string? shaderDirectory) =>
        TryResolveFiles(MpvAnime4KPreset.A, shaderDirectory, out _, out _);

    /// <summary>mpv path list options use ';' on Windows. Always pass absolute paths
    /// with forward slashes so embedded libmpv never resolves them against its own
    /// config directory.</summary>
    public static string ToMpvPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    public static string BuildShaderListValue(IEnumerable<string> paths) =>
        string.Join(';', paths.Select(ToMpvPath));
}

public static class MpvQualityEnhancementPlanner
{
    private static readonly string[] LowUpscaleArguments =
    [
        "--scale=bilinear",
        "--cscale=bilinear",
        "--dscale=bilinear",
        "--correct-downscaling=no",
        "--linear-downscaling=no",
        "--sigmoid-upscaling=no",
    ];

    private static readonly string[] MediumUpscaleArguments =
    [
        "--scale=lanczos",
        "--cscale=spline36",
        "--dscale=mitchell",
        "--correct-downscaling=yes",
        "--linear-downscaling=yes",
        "--sigmoid-upscaling=yes",
    ];

    private static readonly string[] HighUpscaleArguments =
    [
        "--scale=ewa_lanczossharp",
        "--cscale=lanczos",
        "--dscale=mitchell",
        "--scale-antiring=0.6",
        "--correct-downscaling=yes",
        "--linear-downscaling=yes",
        "--sigmoid-upscaling=yes",
    ];

    /// <summary>Frame mixing (mpv smoothmotion style), explicitly NOT MEMC. The
    /// interpolation flag must be paired with display-resample; hwdec-extra-frames
    /// avoids a too-small hardware decoder surface pool once interpolation is on.</summary>
    private static readonly string[] InterpolationArguments =
    [
        "--video-sync=display-resample",
        "--interpolation=yes",
        "--tscale=oversample",
        "--hwdec-extra-frames=8",
    ];

    public static IReadOnlyList<string> BuildInitialArguments(MpvQualityEnhancementOptions? options)
    {
        if (options is null) return [];

        var arguments = new List<string>();
        switch (options.Upscale)
        {
            case MpvUpscaleQualityPreset.Low:
                arguments.AddRange(LowUpscaleArguments);
                break;
            case MpvUpscaleQualityPreset.Medium:
                arguments.AddRange(MediumUpscaleArguments);
                break;
            case MpvUpscaleQualityPreset.High:
                arguments.AddRange(HighUpscaleArguments);
                break;
        }

        switch (options.Interpolation)
        {
            case MpvFrameInterpolationMode.MpvBuiltIn:
                arguments.AddRange(InterpolationArguments);
                break;
            case MpvFrameInterpolationMode.Rife:
                // RIFE runs through the vapoursynth filter, which is added as a runtime
                // command once the enhancement pack plan is available (see LibMpvPlaybackSession).
                break;
        }
        return arguments;
    }

    public static bool IsHdrContent(MpvVideoTrackInfo? video, double? signalPeak)
    {
        if (video?.IsHdr == true) return true;
        return signalPeak is { } peak && peak > 1.0;
    }

    public static MpvQualityShaderPlan PlanShaders(
        MpvQualityEnhancementOptions? options,
        MpvDisplayPipeline displayPipeline,
        MpvVideoTrackInfo? video,
        double? signalPeak,
        string? shaderDirectory = null)
    {
        if (options is null || options.Anime4K == MpvAnime4KPreset.Off)
            return new(MpvQualityShaderDecision.NotRequested, []);

        if (displayPipeline == MpvDisplayPipeline.HdrPassthrough)
        {
            return new(
                MpvQualityShaderDecision.SkipHdr,
                [],
                MpvQualityDegradedReasons.HdrContent);
        }

        // video-params may not be populated before the first frame; the session
        // retries until transfer/primaries/size are known.
        if (video is null && signalPeak is null)
            return new(MpvQualityShaderDecision.NotReady, []);

        if (IsHdrContent(video, signalPeak))
        {
            return new(
                MpvQualityShaderDecision.SkipHdr,
                [],
                MpvQualityDegradedReasons.HdrContent);
        }

        var directory = shaderDirectory ?? options.ShaderDirectory;
        if (!MpvAnime4KShaderCatalog.TryResolveFiles(options.Anime4K, directory, out var paths, out var missing))
        {
            return new(
                MpvQualityShaderDecision.SkipAssets,
                [],
                MpvQualityDegradedReasons.AssetsMissing,
                missing);
        }

        return new(MpvQualityShaderDecision.Apply, paths);
    }

    public static MpvQualityEnhancementStatus BuildStatus(
        MpvQualityEnhancementOptions options,
        MpvQualityShaderPlan plan,
        string? shaderStateOverride = null,
        string? degradedReasonOverride = null,
        string? rifeState = null,
        string? rifeDegradedReasonKey = null,
        int? rifeFactor = null,
        string? rifeProcessingHeight = null) =>
        new(
            MpvQualityPresetSettings.ToStorageValue(options.Upscale),
            MpvQualityPresetSettings.ToStorageValue(options.Anime4K),
            MpvFrameInterpolationModes.ToStorageValue(options.Interpolation),
            shaderStateOverride ?? MpvQualityShaderStates.FromDecision(plan.Decision),
            degradedReasonOverride ?? plan.DegradedReasonKey,
            plan.Decision == MpvQualityShaderDecision.Apply
                ? plan.ShaderPaths.Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToArray()
                : [],
            RifeState: rifeState,
            RifeDegradedReasonKey: rifeDegradedReasonKey,
            RifeFactor: rifeFactor,
            RifeProcessingHeight: rifeProcessingHeight);
}