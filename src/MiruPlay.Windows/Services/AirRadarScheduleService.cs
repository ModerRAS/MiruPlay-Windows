using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record AirRadarScheduleItem(
    int SubjectId,
    string Name,
    string? NameCn,
    string? AirDate,
    string? Image,
    int Weekday,
    string? Time,
    string? Timezone,
    string? Confidence);

public sealed record AirRadarScheduleData(
    int SchemaVersion,
    string? GeneratedAt,
    string? Season,
    string? SeasonStart,
    string? SeasonEnd,
    string? Timezone,
    int ItemCount,
    IReadOnlyList<AirRadarScheduleItem> Items);

public sealed record AirRadarLocalEntry(
    AirRadarScheduleItem Item,
    DateTimeOffset LocalTime,
    string TimeText);

public sealed record AirRadarScheduleDay(
    DayOfWeek Weekday,
    IReadOnlyList<AirRadarLocalEntry> Entries);

public sealed record AirRadarScheduleLoadResult(
    AirRadarScheduleData? Data,
    bool Stale,
    string? Error);

/// <summary>
/// Consumer for the optional AirRadar schedule API. The base URL lives only in
/// AppSettings (never in the repo); responses are cached in the per-user cache
/// directory for one hour and converted from JST to the device time zone.
/// </summary>
public sealed class AirRadarScheduleService : IDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeZoneInfo Jst = CreateJstZone();
    private readonly Func<string> _getBaseUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _localZone;

    public AirRadarScheduleService(
        Func<string> getBaseUrl,
        HttpMessageHandler? handler = null,
        string? cacheDirectory = null,
        Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? localTimeZone = null,
        HttpClient? httpClient = null)
    {
        _getBaseUrl = getBaseUrl;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is not null)
        {
            Timeout = RequestTimeout,
        };
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay", "cache");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _localZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    public static string NormalizeBaseUrl(string? value) => AppSettingsStore.NormalizeScheduleDataSourceUrl(value);

    public async Task<AirRadarScheduleLoadResult> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(_getBaseUrl());
        if (baseUrl.Length == 0) return new AirRadarScheduleLoadResult(null, false, "not_configured");
        var cachePath = CachePathFor(baseUrl);
        var cache = ReadCache(cachePath);
        if (!forceRefresh && cache is not null && _clock() - cache.Value.SavedAt < CacheTtl)
            return new AirRadarScheduleLoadResult(cache.Value.Data, false, null);

        try
        {
            var requestUri = new Uri(baseUrl.TrimEnd('/') + "/api/schedule/latest", UriKind.Absolute);
            using var response = await _http.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // No schedule data published yet; prefer stale cache if present.
                if (cache is not null) return new AirRadarScheduleLoadResult(cache.Value.Data, true, "no_schedule_data");
                return new AirRadarScheduleLoadResult(null, false, "no_schedule_data");
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > 2 * 1024 * 1024) throw new InvalidDataException("schedule response too large");
            }
            var payload = Encoding.UTF8.GetString(buffer.ToArray());
            var data = Parse(payload);
            WriteCache(cachePath, data, payload);
            return new AirRadarScheduleLoadResult(data, false, null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
        {
            if (cache is not null) return new AirRadarScheduleLoadResult(cache.Value.Data, true, error.Message);
            return new AirRadarScheduleLoadResult(null, false, error.Message);
        }
    }

    public async Task<byte[]?> GetImageAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var response = await _http.GetAsync(url.Trim(), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length is > 0 and <= 4 * 1024 * 1024 ? bytes : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public static AirRadarScheduleData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AirRadar response is not an object.");
        var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) && schemaElement.TryGetInt32(out var schema)
            ? schema
            : 1;
        if (schemaVersion != 1) throw new InvalidDataException($"Unsupported AirRadar schemaVersion: {schemaVersion}");
        var items = new List<AirRadarScheduleItem>();
        if (root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                var item = ParseItem(itemElement);
                if (item is not null) items.Add(item);
            }
        }
        return new AirRadarScheduleData(
            schemaVersion,
            Text(root, "generatedAt"),
            Text(root, "season"),
            Text(root, "seasonStart"),
            Text(root, "seasonEnd"),
            Text(root, "timezone") ?? "Asia/Tokyo",
            root.TryGetProperty("itemCount", out var countElement) && countElement.TryGetInt32(out var count) ? count : items.Count,
            items);
    }

    public static IReadOnlyList<AirRadarScheduleDay> BuildWeek(AirRadarScheduleData data, TimeZoneInfo? localZone = null)
    {
        var zone = localZone ?? TimeZoneInfo.Local;
        var groups = new List<AirRadarLocalEntry>[7];
        for (var i = 0; i < groups.Length; i++) groups[i] = [];
        foreach (var item in data.Items)
        {
            if (ToLocal(item, zone) is not { } localTime) continue;
            var index = localTime.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)localTime.DayOfWeek - 1;
            groups[index].Add(new AirRadarLocalEntry(
                item,
                localTime,
                string.IsNullOrWhiteSpace(item.Time) ? string.Empty : localTime.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        var days = new List<AirRadarScheduleDay>(7);
        for (var i = 0; i < 7; i++)
        {
            var weekday = i == 6 ? DayOfWeek.Sunday : (DayOfWeek)(i + 1);
            var entries = groups[i]
                .OrderBy(entry => entry.Item.Time is null ? 1 : 0)
                .ThenBy(entry => entry.LocalTime)
                .ThenBy(entry => entry.Item.DisplayName(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            days.Add(new AirRadarScheduleDay(weekday, entries));
        }
        return days;
    }

    public static DateTimeOffset? ToLocal(AirRadarScheduleItem item, TimeZoneInfo? localZone = null, TimeZoneInfo? sourceZone = null)
    {
        if (string.IsNullOrWhiteSpace(item.AirDate) ||
            !DateOnly.TryParseExact(item.AirDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        var time = TimeOnly.MinValue;
        if (!string.IsNullOrWhiteSpace(item.Time) &&
            !TimeOnly.TryParseExact(item.Time.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return null;
        var jst = sourceZone ?? Jst;
        var instant = new DateTimeOffset(date.ToDateTime(time), jst.GetUtcOffset(date.ToDateTime(time)));
        return TimeZoneInfo.ConvertTime(instant, localZone ?? TimeZoneInfo.Local);
    }

    public static LibrarySeries? MatchLibrary(IEnumerable<LibrarySeries> series, AirRadarScheduleItem item) =>
        series.FirstOrDefault(candidate => candidate.ExternalIds.Any(id =>
            id.Provider.Equals("Bangumi", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value == item.SubjectId));

    private static AirRadarScheduleItem? ParseItem(JsonElement element)
    {
        if (!element.TryGetProperty("subjectId", out var idElement) || !idElement.TryGetInt32(out var subjectId) || subjectId <= 0)
            return null;
        var name = Text(element, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var weekday = element.TryGetProperty("weekday", out var weekdayElement) && weekdayElement.TryGetInt32(out var value) && value is >= 1 and <= 7
            ? value
            : 0;
        return new AirRadarScheduleItem(
            subjectId,
            name!,
            Text(element, "nameCn"),
            Text(element, "airDate"),
            Text(element, "image"),
            weekday,
            Text(element, "time"),
            Text(element, "timezone"),
            Text(element, "confidence"));
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : null;

    private string CachePathFor(string baseUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl)))[..16].ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"airradar-{hash}.json");
    }

    private static (AirRadarScheduleData Data, DateTimeOffset SavedAt)? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("savedAtUtc", out var savedElement) ||
                !savedElement.TryGetDateTimeOffset(out var savedAt) ||
                !document.RootElement.TryGetProperty("payload", out var payloadElement) ||
                payloadElement.GetString() is not { Length: > 0 } payload)
                return null;
            return (Parse(payload), savedAt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private void WriteCache(string path, AirRadarScheduleData data, string payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var entry = new Dictionary<string, object?>
            {
                ["savedAtUtc"] = _clock(),
                ["payload"] = payload,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(entry));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Cache is an optimization only.
        }
    }

    private static TimeZoneInfo CreateJstZone()
    {
        foreach (var id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "Japan Standard Time", "Japan Standard Time");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public static class AirRadarScheduleItemExtensions
{
    public static string DisplayName(this AirRadarScheduleItem item) =>
        string.IsNullOrWhiteSpace(item.NameCn) ? item.Name : item.NameCn;
}