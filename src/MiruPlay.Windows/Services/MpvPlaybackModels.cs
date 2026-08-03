using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record MpvPlaybackCommand(
    string Command,
    long? PositionMs = null,
    long? DeltaMs = null,
    float? Speed = null,
    int? SubtitleTrackId = null,
    int? AudioTrackId = null);

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

public sealed record MpvWindowsVideoOptions(
    MpvDisplayPipeline DisplayPipeline = MpvDisplayPipeline.Auto,
    bool HardwareDecode = true,
    MpvToneMappingCurve ToneMappingCurve = MpvToneMappingCurve.Mobius,
    int TargetSdrNits = 100,
    int TargetHdrPeakNits = 1_000,
    bool ComputeHdrPeak = true)
{
    public int SafeTargetSdrNits => Math.Clamp(TargetSdrNits, 80, 240);
    public int SafeTargetHdrPeakNits => Math.Clamp(TargetHdrPeakNits, 203, 10_000);
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
        ? (string.IsNullOrWhiteSpace(Language) || Language == "und" ? $"音轨 {Id}" : Language)
        : Title;
}

public sealed record MpvVideoTrackInfo(
    string? Codec,
    string? PixelFormat,
    string? Primaries,
    string? Transfer,
    int? Width,
    int? Height)
{
    public bool IsHdr => string.Equals(Transfer, "pq", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Transfer, "hlg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Primaries, "bt.2020", StringComparison.OrdinalIgnoreCase);
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
    int? SelectedSubtitleTrackId);

public enum MpvFallbackMode
{
    Mpv,
    SystemPlayerDegraded,
}

public sealed record MpvPlaybackLaunchResult(
    MpvPlaybackSession? Session,
    MpvFallbackMode Mode,
    IReadOnlyList<string> DegradedCapabilities)
{
    public bool UsedSystemPlayerFallback => Mode == MpvFallbackMode.SystemPlayerDegraded;
}

public static class MpvWindowsVideoOptionMapper
{
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
                arguments.Add("--target-prim=bt.2020");
                arguments.Add("--target-trc=pq");
                arguments.Add($"--target-peak={options.SafeTargetHdrPeakNits}");
                arguments.Add("--tone-mapping=clip");
                break;
        }

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
