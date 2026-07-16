using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record AppSettings(
    string? LibraryRoot = null,
    string? PlayerPath = null,
    string PreferredSubtitleLanguage = "auto",
    string PlaybackEndAction = "return_to_detail",
    string CurrentAppMode = "anime",
    bool WebControlEnabled = true,
    int WebControlPort = 9978,
    long? ActiveSourceId = null,
    int MediaSourceSchemaVersion = 0,
    IReadOnlyList<MediaSourceDefinition>? MediaSources = null);

public sealed class AppSettingsStore
{
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
        if (!File.Exists(_settingsPath)) return new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings();
            var needsCleanup = json.Contains("\"TypeLabel\"", StringComparison.Ordinal) ||
                json.Contains("\"StatusText\"", StringComparison.Ordinal);
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
            return new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        }
    }

    public void Save(AppSettings settings)
    {
        var tempPath = $"{_settingsPath}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, _settingsPath, true);
    }
}
