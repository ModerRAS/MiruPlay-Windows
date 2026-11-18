using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record MpvPlaybackCommand(
    string Command,
    long? PositionMs = null,
    long? DeltaMs = null,
    float? Speed = null,
    int? SubtitleTrackId = null,
    int? AudioTrackId = null,
    double? VolumePercent = null,
    bool? IsMuted = null);

public enum MpvDisplayPipeline
{
    Auto,
    SdrToneMap,
    HdrPassthrough,
}

public enum MpvToneMappingCurve
{
    Clip,
    Mobius,
    Reinhard,
}

public enum MpvAudioPassthroughMode
{
    Off,
    Auto,
    Force,
}

public sealed record MpvWindowsVideoOptions(
    MpvDisplayPipeline DisplayPipeline = MpvDisplayPipeline.Auto,
    bool HardwareDecode = true,
    MpvToneMappingCurve ToneMappingCurve = MpvToneMappingCurve.Mobius,
    int TargetSdrNits = 100,
    int TargetHdrPeakNits = 1_000,
    bool ComputeHdrPeak = true,
    MpvQualityEnhancementOptions? Quality = null)
{
    public int SafeTargetSdrNits => Math.Clamp(TargetSdrNits, 80, 240);
    public int SafeTargetHdrPeakNits => Math.Clamp(TargetHdrPeakNits, 203, 10_000);
}

/// <summary>Storage-value contract for the HDR display mode shared by
/// settings, WebControl and the mpv option mapper.</summary>
public static class MpvPlaybackModeSettings
{
    public const string HdrModeAuto = "auto";
    public const string HdrModePassthrough = "passthrough";
    public const string HdrModeSdrToneMap = "sdr_tonemap";

    public static IReadOnlyList<string> HdrModeValues { get; } =
        [HdrModeAuto, HdrModePassthrough, HdrModeSdrToneMap];

    public static string NormalizeHdrMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        HdrModePassthrough or "hdr_passthrough" or "source" => HdrModePassthrough,
        HdrModeSdrToneMap or "sdr-tonemap" or "tone_map" or "tonemap" => HdrModeSdrToneMap,
        _ => HdrModeAuto,
    };

    public static MpvDisplayPipeline ToDisplayPipeline(string? hdrMode) => NormalizeHdrMode(hdrMode) switch
    {
        HdrModePassthrough => MpvDisplayPipeline.HdrPassthrough,
        HdrModeSdrToneMap => MpvDisplayPipeline.SdrToneMap,
        _ => MpvDisplayPipeline.Auto,
    };
}

/// <summary>Maps the audio bitstream passthrough settings to fixed mpv options.</summary>
public static class MpvAudioPassthroughOptionMapper
{
    public static IReadOnlyList<string> SupportedCodecs { get; } =
        ["truehd", "eac3", "ac3", "dts", "dts-hd"];

    public static string DefaultCodecs { get; } = string.Join(",", SupportedCodecs);

    public static IReadOnlyList<string> NormalizeCodecs(IEnumerable<string>? codecs)
    {
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (codecs is not null)
        {
            foreach (var codec in codecs)
            {
                var normalized = codec?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(normalized)) requested.Add(normalized);
            }
        }

        if (requested.Count == 0) return SupportedCodecs;
        return SupportedCodecs.Where(requested.Contains).ToArray();
    }

    public static IReadOnlyList<string> ParseCodecs(string? storageValue) =>
        string.IsNullOrWhiteSpace(storageValue)
            ? SupportedCodecs
            : NormalizeCodecs(storageValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string ToStorageCodecs(string? storageValue) => string.Join(",", ParseCodecs(storageValue));

    public static string ToStorageCodecs(IEnumerable<string>? codecs) => string.Join(",", NormalizeCodecs(codecs));

    public static MpvAudioPassthroughMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => MpvAudioPassthroughMode.Auto,
        "force" => MpvAudioPassthroughMode.Force,
        _ => MpvAudioPassthroughMode.Off,
    };

    public static string ToStorageMode(MpvAudioPassthroughMode mode) => mode switch
    {
        MpvAudioPassthroughMode.Auto => "auto",
        MpvAudioPassthroughMode.Force => "force",
        _ => "off",
    };

    public static string NormalizeDevice(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
            return "auto";
        return trimmed.StartsWith("wasapi/", StringComparison.OrdinalIgnoreCase) ? trimmed : "auto";
    }

    public static bool IsEnabled(MpvAudioPassthroughMode mode) => mode != MpvAudioPassthroughMode.Off;

    public static bool IsPassthroughFormat(string? audioOutputFormat) =>
        audioOutputFormat?.Trim().StartsWith("spdif-", StringComparison.OrdinalIgnoreCase) == true;

    public static IReadOnlyList<string> BuildArguments(
        MpvAudioPassthroughMode mode,
        string? storedCodecs = null,
        string? device = null)
    {
        if (!IsEnabled(mode)) return [];

        var arguments = new List<string>
        {
            "--ao=wasapi",
            "--audio-exclusive=yes",
            $"--audio-spdif={string.Join(",", ParseCodecs(storedCodecs))}",
        };
        var normalizedDevice = NormalizeDevice(device);
        if (!string.Equals(normalizedDevice, "auto", StringComparison.Ordinal))
            arguments.Add($"--audio-device={normalizedDevice}");
        return arguments;
    }
}

public sealed record MpvAudioTrack(
    int Id,
    string Language,
    string Title,
    string Codec,
    bool IsExternal,
    bool IsSelected)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Title)
        ? (string.IsNullOrWhiteSpace(Language) || Language == "und" ? AppStrings.Get("play.trackLabel", Id) : Language)
        : Title;
}

public sealed record MpvVideoTrackInfo(
    string? Codec,
    string? PixelFormat,
    string? Primaries,
    string? Transfer,
    int? Width,
    int? Height,
    int? DolbyVisionProfile = null,
    int? DolbyVisionLevel = null)
{
    public bool IsHdr => string.Equals(Transfer, "pq", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Transfer, "hlg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Primaries, "bt.2020", StringComparison.OrdinalIgnoreCase);

    public bool IsDolbyVision => DolbyVisionProfile is not null;
}

public sealed record MpvPlaybackInfo(
    long PositionMs,
    long DurationMs,
    bool IsPaused,
    double Speed,
    string? FileName,
    string? MediaTitle,
    string? ContainerFormat,
    string? AudioCodec,
    MpvVideoTrackInfo? Video,
    IReadOnlyList<MpvAudioTrack> AudioTracks,
    IReadOnlyList<PlaybackSubtitleTrack> SubtitleTracks,
    int? SelectedAudioTrackId,
    int? SelectedSubtitleTrackId,
    double VolumePercent,
    bool IsMuted,
    string? AudioOutputFormat = null,
    string? AudioOutputChannels = null,
    bool AudioPassthroughActive = false,
    bool EnhancementLayerDegraded = false);

public enum MpvFallbackMode
{
    LibMpvEmbedded,
    SystemPlayerDegraded,
}

public sealed record MpvPlaybackLaunchResult(
    IPlaybackSession? Session,
    MpvFallbackMode Mode,
    IReadOnlyList<string> DegradedCapabilities)
{
    public bool UsedSystemPlayerFallback => Mode == MpvFallbackMode.SystemPlayerDegraded;
}

public static class MpvWindowsVideoOptionMapper
{
    public static MpvWindowsVideoOptions FromSettings(AppSettings settings, string? shaderDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var quality = new MpvQualityEnhancementOptions(
            MpvQualityPresetSettings.ParseUpscale(settings.MpvUpscalePreset),
            MpvQualityPresetSettings.ParseAnime4K(settings.MpvAnime4KPreset),
            settings.MpvInterpolationEnabled,
            shaderDirectory ?? MpvQualityEnhancementPaths.ResolveShaderDirectory());
        return new MpvWindowsVideoOptions(
            MpvPlaybackModeSettings.ToDisplayPipeline(settings.HdrMode),
            Quality: quality);
    }

    public static IReadOnlyList<string> BuildArguments(MpvWindowsVideoOptions? options)
    {
        options ??= new();
        var arguments = new List<string>
        {
            "--vo=gpu-next",
            "--gpu-api=d3d11",
            $"--hwdec={(options.HardwareDecode ? "auto-safe" : "no")}",
        };

        switch (options.DisplayPipeline)
        {
            case MpvDisplayPipeline.SdrToneMap:
                arguments.Add("--target-prim=bt.709");
                arguments.Add("--target-trc=bt.1886");
                arguments.Add($"--target-peak={options.SafeTargetSdrNits}");
                arguments.Add($"--tone-mapping={ToneMappingName(options.ToneMappingCurve)}");
                if (options.ToneMappingCurve != MpvToneMappingCurve.Clip)
                    arguments.Add($"--tone-mapping-param={ToneMappingParameter(options.ToneMappingCurve):0.###}");
                arguments.Add("--gamut-mapping-mode=perceptual");
                arguments.Add($"--hdr-compute-peak={(options.ComputeHdrPeak ? "yes" : "no")}");
                break;
            case MpvDisplayPipeline.HdrPassthrough:
                // "HDR passthrough" for the D3D11/libplacebo stack: signal the source
                // colorspace on the DXGI swapchain and do not tone-map internally.
                // target-peak=10000 is the PQ nominal peak, so the Windows-reported
                // display peak cannot trigger implicit dynamic tone mapping.
                arguments.Add("--target-colorspace-hint=yes");
                arguments.Add("--target-colorspace-hint-mode=source");
                arguments.Add("--target-peak=10000");
                arguments.Add("--tone-mapping=clip");
                arguments.Add("--hdr-compute-peak=no");
                break;
        }

        arguments.AddRange(MpvQualityEnhancementPlanner.BuildInitialArguments(options.Quality));
        return arguments;
    }

    private static string ToneMappingName(MpvToneMappingCurve curve) => curve switch
    {
        MpvToneMappingCurve.Clip => "clip",
        MpvToneMappingCurve.Mobius => "mobius",
        MpvToneMappingCurve.Reinhard => "reinhard",
        _ => throw new ArgumentOutOfRangeException(nameof(curve)),
    };

    private static double ToneMappingParameter(MpvToneMappingCurve curve) => curve switch
    {
        MpvToneMappingCurve.Mobius => 0.4,
        MpvToneMappingCurve.Reinhard => 0.5,
        _ => 0,
    };
}
