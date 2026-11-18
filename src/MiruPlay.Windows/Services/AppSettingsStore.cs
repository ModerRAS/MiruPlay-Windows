using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Services;

public sealed record AppSettings(
    string? LibraryRoot = null,
    string PreferredSubtitleLanguage = "auto",
    string PlaybackEndAction = "return_to_detail",
    string CurrentAppMode = "anime",
    bool WebControlEnabled = true,
    int WebControlPort = 9978,
    long? ActiveSourceId = null,
    int MediaSourceSchemaVersion = 0,
    IReadOnlyList<MediaSourceDefinition>? MediaSources = null,
    bool AutoScanEnabled = false,
    int AutoScanIntervalHours = 6,
    bool LogUploadEnabled = false,
    string LogUploadEndpoint = "",
    string LogUploadStreamName = "miruplay",
    long LastLogUploadAt = 0,
    string? LastLogUploadStatus = null,
    AudioDspConfig? AudioDsp = null,
    string? LibMpvPath = null,
    bool MergeSameAnimeEnabled = false,
    string PosterWallArrangement = "TITLE",
    string EpisodeVersionSelectionPolicy = "auto_nearest",
    string AppUpdateManifestUrl = "",
    string AutoUpdateMode = "download",
    int AutoUpdateIntervalHours = 6,
    string AudioSrcBypass = "software",
    int AudioOutputSampleRate = 48000,
    string AppLanguage = "system",
    bool TvModeEnabled = false,
    int TvUiScalePercent = 100,
    bool TvSoundFeedback = true,
    bool TvClockVisible = true,
    string TvRemotePreset = "general",
    string TvVolumeKeyTarget = "system",
    int TvExitLongPressMs = 2000,
    bool TvGamepadEnabled = true,
    int TvGamepadDeadzonePercent = 50,
    string TvGamepadFaceLayout = "xbox",
    string AudioBalance = "off",
    string HdrMode = "auto",
    string AudioPassthroughMode = "off",
    string AudioPassthroughCodecs = "truehd,eac3,ac3,dts,dts-hd",
    string AudioPassthroughDevice = "auto",
    string MpvUpscalePreset = "off",
    string MpvAnime4KPreset = "off",
    string FrameInterpolationMode = "off",
    RifeEnhancementSettings? RifeEnhancement = null);

public sealed class AppSettingsStore
{
    private const int MaxSettingsBytes = 1 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public AppSettingsStore(string? settingsPath = null)
    {
        var directory = settingsPath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay")
            : Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        Directory.CreateDirectory(directory);
        _settingsPath = settingsPath is null ? Path.Combine(directory, "settings.json") : Path.GetFullPath(settingsPath);
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath)) return new AppSettings(
            MediaSourceSchemaVersion: 1,
            MediaSources: [],
            AudioDsp: AudioDspConfig.Neutral());

        try
        {
            var fileInfo = new FileInfo(_settingsPath);
            if (fileInfo.Length > MaxSettingsBytes) throw new InvalidDataException(AppStrings.Get("settings.tooLarge"));
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings();
            var needsCleanup = json.Contains("\"TypeLabel\"", StringComparison.Ordinal) ||
                json.Contains("\"StatusText\"", StringComparison.Ordinal);
            var normalizedDsp = (settings.AudioDsp ?? AudioDspConfig.Neutral()).Normalize();
            if (settings.AudioDsp is null ||
                !string.Equals(
                    JsonSerializer.Serialize(settings.AudioDsp, JsonOptions),
                    JsonSerializer.Serialize(normalizedDsp, JsonOptions),
                    StringComparison.Ordinal))
            {
                settings = settings with { AudioDsp = normalizedDsp };
                needsCleanup = true;
            }
            if (settings.MediaSourceSchemaVersion == 0)
            {
                var sources = string.IsNullOrWhiteSpace(settings.LibraryRoot)
                    ? []
                    : new List<MediaSourceDefinition>
                    {
                        new(
                            1,
                            Path.GetFileName(settings.LibraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            "LOCAL",
                            Path.GetFullPath(settings.LibraryRoot),
                            IsConnected: File.Exists(Path.Combine(settings.LibraryRoot, "library.db"))),
                    };
                settings = settings with
                {
                    ActiveSourceId = sources.FirstOrDefault()?.Id,
                    MediaSourceSchemaVersion = 1,
                    MediaSources = sources,
                };
                needsCleanup = true;
            }
            var normalizedMode = string.Equals(settings.CurrentAppMode, "drama", StringComparison.OrdinalIgnoreCase)
                ? "drama"
                : "anime";
            if (settings.CurrentAppMode != normalizedMode)
            {
                settings = settings with { CurrentAppMode = normalizedMode };
                needsCleanup = true;
            }
            var normalizedInterval = MediaSourceAutoScanScheduler.NormalizeIntervalHours(settings.AutoScanIntervalHours);
            if (settings.AutoScanIntervalHours != normalizedInterval)
            {
                settings = settings with { AutoScanIntervalHours = normalizedInterval };
                needsCleanup = true;
            }
            if (!LibraryOrganization.ArrangementOptions.Contains(settings.PosterWallArrangement))
            {
                settings = settings with { PosterWallArrangement = "TITLE" };
                needsCleanup = true;
            }
            if (settings.EpisodeVersionSelectionPolicy is not ("auto_nearest" or "manual"))
            {
                settings = settings with { EpisodeVersionSelectionPolicy = "auto_nearest" };
                needsCleanup = true;
            }
            var normalizedSrcBypass = NormalizeAudioSrcBypass(settings.AudioSrcBypass);
            var normalizedSampleRate = NormalizeAudioOutputSampleRate(settings.AudioOutputSampleRate);
            var normalizedLanguage = AppLocale.Normalize(settings.AppLanguage);
            var normalizedTvPreset = TvKeyMap.NormalizePresetId(settings.TvRemotePreset);
            var normalizedTvScale = settings.TvUiScalePercent is 100 or 125 or 150 ? settings.TvUiScalePercent : 100;
            var normalizedTvVolumeTarget = settings.TvVolumeKeyTarget is "system" or "application" ? settings.TvVolumeKeyTarget : "system";
            var normalizedTvExitHold = Math.Clamp(settings.TvExitLongPressMs, 1000, 5000);
            var normalizedAutoUpdateMode = AutoUpdateScheduler.NormalizeMode(settings.AutoUpdateMode);
            var normalizedAutoUpdateInterval = Math.Clamp(settings.AutoUpdateIntervalHours, 1, 168);
            var normalizedGamepadDeadzone = settings.TvGamepadDeadzonePercent is >= 20 and <= 80
                ? settings.TvGamepadDeadzonePercent
                : 50;
            var normalizedGamepadLayout = TvGamepadFaceLayouts.Normalize(settings.TvGamepadFaceLayout);
            var normalizedBalance = NormalizeAudioBalance(settings.AudioBalance);
            var normalizedHdrMode = NormalizeHdrMode(settings.HdrMode);
            var normalizedPassthroughMode = NormalizeAudioPassthroughMode(settings.AudioPassthroughMode);
            var normalizedPassthroughCodecs = NormalizeAudioPassthroughCodecs(settings.AudioPassthroughCodecs);
            var normalizedPassthroughDevice = NormalizeAudioPassthroughDevice(settings.AudioPassthroughDevice);
            var normalizedUpscalePreset = NormalizeMpvUpscalePreset(settings.MpvUpscalePreset);
            var normalizedAnime4KPreset = NormalizeMpvAnime4KPreset(settings.MpvAnime4KPreset);
            // Before the interpolation mode became extensible it was stored as a bool.
            // Migrate a legacy true value to the built-in mpv mode once.
            var legacyInterpolationEnabled =
                json.Contains("\"MpvInterpolationEnabled\":true", StringComparison.Ordinal) ||
                json.Contains("\"MpvInterpolationEnabled\": true", StringComparison.Ordinal);
            var normalizedFrameInterpolation = legacyInterpolationEnabled &&
                string.Equals(settings.FrameInterpolationMode, MpvFrameInterpolationModes.Off, StringComparison.Ordinal)
                    ? MpvFrameInterpolationModes.MpvBuiltIn
                    : NormalizeFrameInterpolationMode(settings.FrameInterpolationMode);
            var normalizedRifeEnhancement = RifeEnhancementSettings.Normalize(settings.RifeEnhancement);
            if (settings.AudioSrcBypass != normalizedSrcBypass || settings.AudioOutputSampleRate != normalizedSampleRate ||
                settings.AppLanguage != normalizedLanguage ||
                settings.TvRemotePreset != normalizedTvPreset ||
                settings.TvUiScalePercent != normalizedTvScale ||
                settings.TvVolumeKeyTarget != normalizedTvVolumeTarget ||
                settings.TvExitLongPressMs != normalizedTvExitHold ||
                settings.AutoUpdateMode != normalizedAutoUpdateMode ||
                settings.AutoUpdateIntervalHours != normalizedAutoUpdateInterval ||
                settings.AudioBalance != normalizedBalance ||
                settings.HdrMode != normalizedHdrMode ||
                settings.AudioPassthroughMode != normalizedPassthroughMode ||
                settings.AudioPassthroughCodecs != normalizedPassthroughCodecs ||
                settings.AudioPassthroughDevice != normalizedPassthroughDevice ||
                settings.MpvUpscalePreset != normalizedUpscalePreset ||
                settings.MpvAnime4KPreset != normalizedAnime4KPreset ||
                settings.FrameInterpolationMode != normalizedFrameInterpolation ||
                settings.RifeEnhancement is null ||
                !settings.RifeEnhancement.Equals(normalizedRifeEnhancement))
            {
                settings = settings with
                {
                    AudioSrcBypass = normalizedSrcBypass,
                    AudioOutputSampleRate = normalizedSampleRate,
                    AppLanguage = normalizedLanguage,
                    TvRemotePreset = normalizedTvPreset,
                    TvUiScalePercent = normalizedTvScale,
                    TvVolumeKeyTarget = normalizedTvVolumeTarget,
                    TvExitLongPressMs = normalizedTvExitHold,
                    AutoUpdateMode = normalizedAutoUpdateMode,
                    AutoUpdateIntervalHours = normalizedAutoUpdateInterval,
                    AudioBalance = normalizedBalance,
                    HdrMode = normalizedHdrMode,
                    AudioPassthroughMode = normalizedPassthroughMode,
                    AudioPassthroughCodecs = normalizedPassthroughCodecs,
                    AudioPassthroughDevice = normalizedPassthroughDevice,
                    MpvUpscalePreset = normalizedUpscalePreset,
                    MpvAnime4KPreset = normalizedAnime4KPreset,
                    FrameInterpolationMode = normalizedFrameInterpolation,
                    RifeEnhancement = normalizedRifeEnhancement,
                };
                needsCleanup = true;
            }
            if (settings.ActiveSourceId is null && settings.MediaSources is { Count: > 0 })
            {
                var mode = normalizedMode.ToUpperInvariant();
                settings = settings with
                {
                    ActiveSourceId = settings.MediaSources.FirstOrDefault(source => source.ContentMode == mode)?.Id
                        ?? settings.MediaSources[0].Id,
                };
                needsCleanup = true;
            }
            if (needsCleanup) Save(settings);
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings(
                MediaSourceSchemaVersion: 1,
                MediaSources: [],
                AudioDsp: AudioDspConfig.Neutral());
        }
    }

    public static string NormalizeAudioSrcBypass(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "system" or "software" or "direct" => value.Trim().ToLowerInvariant(),
        _ => "software",
    };

    public static int NormalizeAudioOutputSampleRate(int value) => value is >= 8_000 and <= 384_000 ? value : 48000;

    public static string NormalizeAudioBalance(string? value) =>
        AudioBalanceLevels.ToStorageValue(AudioBalanceLevels.FromStorageValue(value));

    public static string NormalizeHdrMode(string? value) => MpvPlaybackModeSettings.NormalizeHdrMode(value);

    public static string NormalizeAudioPassthroughMode(string? value) =>
        MpvAudioPassthroughOptionMapper.ToStorageMode(MpvAudioPassthroughOptionMapper.ParseMode(value));

    public static string NormalizeAudioPassthroughCodecs(string? value) =>
        MpvAudioPassthroughOptionMapper.ToStorageCodecs(value);

    public static string NormalizeAudioPassthroughDevice(string? value) =>
        MpvAudioPassthroughOptionMapper.NormalizeDevice(value);

    public static string NormalizeMpvUpscalePreset(string? value) =>
        MpvQualityPresetSettings.NormalizeUpscale(value);

    public static string NormalizeMpvAnime4KPreset(string? value) =>
        MpvQualityPresetSettings.NormalizeAnime4K(value);

    public static string NormalizeFrameInterpolationMode(string? value) =>
        MpvFrameInterpolationModes.Normalize(value);

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxSettingsBytes)
            throw new InvalidDataException(AppStrings.Get("settings.tooLarge"));
        var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        File.Move(tempPath, _settingsPath, true);
    }
}