using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Models;

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
    string? LibMpvPath = null);

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
            if (fileInfo.Length > MaxSettingsBytes) throw new InvalidDataException("设置文件过大。");
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

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxSettingsBytes)
            throw new InvalidDataException("设置文件过大。");
        var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        File.Move(tempPath, _settingsPath, true);
    }
}
