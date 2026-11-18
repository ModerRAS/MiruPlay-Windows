using System.Globalization;

namespace MiruPlay.Windows.Services;

public sealed record MpvAudioDeviceOption(string Name, string Description);

/// <summary>Runtime capabilities of the pinned libmpv process, probed once with a
/// throwaway client (no playback, vo/ao=null).</summary>
public sealed record MpvRuntimeCapabilities(
    string? MpvVersion,
    string? FfmpegVersion,
    string? LibPlaceboVersion,
    int? LibPlaceboApiVersion,
    bool DolbyVisionFelSupported,
    bool HdrPassthroughSupported,
    IReadOnlyList<MpvAudioDeviceOption> AudioDevices);

public static class MpvRuntimeCapabilityProbe
{
    /// <summary>Conservative PL_API_VER gate for DV P7 FEL composition (NLQ API 366,
    /// enhancement-layer frame API 367/369; the mpv 0.42 line ships API 387).</summary>
    public const int MinimumFelLibPlaceboApi = 370;

    private static readonly object Sync = new();
    private static string? _cachedPath;
    private static long _cachedLength;
    private static MpvRuntimeCapabilities? _cached;

    public static MpvRuntimeCapabilities? Probe(string? libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath)) return null;
        string fullPath;
        long length;
        try
        {
            fullPath = Path.GetFullPath(libraryPath.Trim());
            if (!File.Exists(fullPath)) return null;
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (Sync)
        {
            if (_cached is not null &&
                string.Equals(_cachedPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                _cachedLength == length)
            {
                return _cached;
            }
        }

        var capabilities = ProbeCore(fullPath);
        lock (Sync)
        {
            _cachedPath = fullPath;
            _cachedLength = length;
            _cached = capabilities;
        }
        return capabilities;
    }

    public static IReadOnlyList<MpvAudioDeviceOption> EnumerateAudioDevices(string? libraryPath) =>
        Probe(libraryPath)?.AudioDevices ?? [];

    private static MpvRuntimeCapabilities? ProbeCore(string libraryPath)
    {
        try
        {
            using var client = LibMpvClient.Create(libraryPath);
            // Probe option availability before initialize(); a missing option returns
            // a negative error code instead of throwing.
            var felOption = client.SetOptionString("vf", "format=enhancement-layer=no") == 0;
            var hdrOption = client.SetOptionString("target-colorspace-hint-mode", "source") == 0;
            client.SetOptionString("vo", "null");
            client.SetOptionString("ao", "null");
            client.SetOptionString("idle", "yes");
            if (client.Initialize() < 0) return null;

            var mpvVersion = client.GetPropertyString("mpv-version");
            var ffmpegVersion = client.GetPropertyString("ffmpeg-version");
            var libPlaceboVersion = client.GetPropertyString("libplacebo-version");
            var libPlaceboApi = ParseLibPlaceboApi(libPlaceboVersion);
            // Dolby Vision P7 FEL composition needs the libplacebo enhancement-layer /
            // NLQ APIs (PL_API_VER 366/367/369). mpv 0.42 documents a 7.387.0 minimum
            // for the release line as a whole; community verification uses API >= 370,
            // which is the conservative gate used here.
            var felSupported = felOption && (libPlaceboApi is null || libPlaceboApi >= MinimumFelLibPlaceboApi);
            return new MpvRuntimeCapabilities(
                mpvVersion,
                ffmpegVersion,
                libPlaceboVersion,
                libPlaceboApi,
                felSupported,
                hdrOption,
                ParseAudioDevices(client.GetPropertyNode("audio-device-list")));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static int? ParseLibPlaceboApi(string? version)
    {
        // libplacebo reports e.g. "v7.377.0 (v7.360.0-176-g...)" where the minor part
        // is the API version (366 is the first version with FEL NLQ composition).
        if (string.IsNullOrWhiteSpace(version)) return null;
        var normalized = version.TrimStart('v', 'V');
        var dot = normalized.IndexOf('.');
        if (dot < 0) return null;
        var end = dot + 1;
        while (end < normalized.Length && char.IsDigit(normalized[end])) end++;
        return int.TryParse(normalized[(dot + 1)..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var api)
            ? api
            : null;
    }

    private static List<MpvAudioDeviceOption> ParseAudioDevices(LibMpvNodeValue? node)
    {
        var values = node?.ArrayValue ?? node?.MapValue?.Values;
        if (values is null) return [];
        var devices = new List<MpvAudioDeviceOption>();
        foreach (var item in values)
        {
            if (item.MapValue is null) continue;
            var name = item.MapValue.GetValueOrDefault("name")?.StringValue;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var description = item.MapValue.GetValueOrDefault("description")?.StringValue;
            devices.Add(new(name, string.IsNullOrWhiteSpace(description) ? name : description));
        }
        return devices;
    }
}
