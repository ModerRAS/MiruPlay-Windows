using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

public static class TvAirWeekdayLabels
{
    public static string Title(TvAirWeekday weekday) => AppStrings.Get(weekday switch
    {
        TvAirWeekday.Monday => "tv.season.weekday.monday",
        TvAirWeekday.Tuesday => "tv.season.weekday.tuesday",
        TvAirWeekday.Wednesday => "tv.season.weekday.wednesday",
        TvAirWeekday.Thursday => "tv.season.weekday.thursday",
        TvAirWeekday.Friday => "tv.season.weekday.friday",
        TvAirWeekday.Saturday => "tv.season.weekday.saturday",
        TvAirWeekday.Sunday => "tv.season.weekday.sunday",
        _ => "tv.season.weekday.unknown",
    });
}

/// <summary>One Bangumi calendar entry (anime airing on a weekday, per bgm.tv/calendar).</summary>
public sealed record TvBangumiCalendarItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("name_cn")] string? NameCn,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("air_date")] string? AirDate,
    [property: JsonPropertyName("air_weekday")] int AirWeekday,
    [property: JsonPropertyName("image")] string? ImageUrl,
    [property: JsonPropertyName("rating")] double Rating)
{
    public string DisplayName => string.IsNullOrWhiteSpace(NameCn) ? Name : NameCn;
}

public sealed record TvBangumiCalendarDay(TvAirWeekday Weekday, IReadOnlyList<TvBangumiCalendarItem> Items);

public sealed record TvBangumiCalendarData(IReadOnlyList<TvBangumiCalendarDay> Days);

public sealed record TvBangumiCalendarLoadResult(
    TvBangumiCalendarData? Data,
    bool Stale,
    bool FromCache,
    string? Error);

public sealed record TvScheduleEntry(
    TvBangumiCalendarItem Item,
    DateOnly Date,
    string DateText,
    bool IsToday);

public sealed record TvScheduleDaySection(
    TvAirWeekday Weekday,
    DateOnly Date,
    bool IsToday,
    IReadOnlyList<TvScheduleEntry> Entries);

/// <summary>
/// Fetches the public Bangumi calendar (https://api.bgm.tv/calendar), caches it
/// for one hour under %LOCALAPPDATA%/MiruPlay/cache and builds the
/// previous/current/next week schedule with "not aired yet" entries filtered.
/// Network access is injectable for tests and reuses the app's outbound proxy
/// settings when constructed from the TV shell.
/// </summary>
public sealed class TvBangumiCalendarService : IDisposable
{
    public const string CalendarUrl = "https://api.bgm.tv/calendar";
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const long MaxResponseBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _disposeHttp;

    public TvBangumiCalendarService(
        HttpMessageHandler? handler = null,
        string? cachePath = null,
        Func<DateTimeOffset>? clock = null,
        HttpClient? httpClient = null)
    {
        _disposeHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is not null)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MiruPlay/0.1 (+https://github.com/ModerRAS/MiruPlay-Windows)");
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay", "cache", "bangumi-calendar.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<TvBangumiCalendarLoadResult> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var (cache, cacheSavedAt) = ReadCache();
        if (!forceRefresh && cache is not null && _clock() - cacheSavedAt < CacheTtl)
            return new TvBangumiCalendarLoadResult(cache, Stale: false, FromCache: true, Error: null);

        try
        {
            using var response = await _http.GetAsync(CalendarUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var limited = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                limited.Write(buffer, 0, read);
                if (limited.Length > MaxResponseBytes) throw new InvalidDataException("Bangumi calendar response is too large.");
            }
            var payload = System.Text.Encoding.UTF8.GetString(limited.ToArray());
            var data = ParseCalendar(payload);
            WriteCache(payload);
            return new TvBangumiCalendarLoadResult(data, Stale: false, FromCache: false, Error: null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
        {
            if (cache is not null)
                return new TvBangumiCalendarLoadResult(cache, Stale: true, FromCache: true, Error: error.Message);
            return new TvBangumiCalendarLoadResult(null, Stale: false, FromCache: false, Error: error.Message);
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

    public static TvBangumiCalendarData ParseCalendar(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Bangumi calendar response is not an array.");
        var days = new List<TvBangumiCalendarDay>();
        foreach (var dayElement in document.RootElement.EnumerateArray())
        {
            var weekday = TvAirWeekday.Unknown;
            if (dayElement.TryGetProperty("weekday", out var weekdayElement) &&
                weekdayElement.TryGetProperty("id", out var idElement) &&
                idElement.TryGetInt32(out var weekdayId) && weekdayId is >= 1 and <= 7)
            {
                weekday = (TvAirWeekday)weekdayId;
            }
            var items = new List<TvBangumiCalendarItem>();
            if (dayElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemElement in itemsElement.EnumerateArray())
                {
                    var item = ParseItem(itemElement);
                    if (item is not null) items.Add(item);
                }
            }
            days.Add(new TvBangumiCalendarDay(weekday, items));
        }
        return new TvBangumiCalendarData(days);
    }

    private static TvBangumiCalendarItem? ParseItem(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || id <= 0) return null;
        var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var nameCn = element.TryGetProperty("name_cn", out var nameCnElement) ? nameCnElement.GetString() : null;
        var summary = element.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
        var airDate = element.TryGetProperty("air_date", out var airDateElement) ? airDateElement.GetString() : null;
        var airWeekday = element.TryGetProperty("air_weekday", out var airWeekdayElement) && airWeekdayElement.TryGetInt32(out var weekday)
            ? weekday
            : 0;
        var imageUrl = element.TryGetProperty("images", out var imagesElement) && imagesElement.ValueKind == JsonValueKind.Object &&
                       imagesElement.TryGetProperty("large", out var largeElement)
            ? largeElement.GetString()
            : null;
        var rating = element.TryGetProperty("rating", out var ratingElement) &&
                     ratingElement.ValueKind == JsonValueKind.Object &&
                     ratingElement.TryGetProperty("score", out var scoreElement) &&
                     scoreElement.TryGetDouble(out var score)
            ? score
            : 0;
        return new TvBangumiCalendarItem(id, name!, Clean(nameCn), Clean(summary), Clean(airDate), airWeekday, Clean(imageUrl), rating);
    }

    private static string? Clean(string? value)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        return clean.Length <= 20_000 ? clean : clean[..20_000];
    }

    public static DateOnly WeekStartFor(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static IReadOnlyList<TvScheduleDaySection> BuildWeek(TvBangumiCalendarData data, DateOnly today, int weekOffset)
    {
        var weekStart = WeekStartFor(today).AddDays(weekOffset * 7);
        var weekEnd = weekStart.AddDays(6);
        var sections = new List<TvScheduleDaySection>(7);
        for (var weekday = 1; weekday <= 7; weekday++)
        {
            var date = weekStart.AddDays(weekday - 1);
            var calendarDay = data.Days.FirstOrDefault(day => day.Weekday == (TvAirWeekday)weekday);
            var entries = new List<TvScheduleEntry>();
            foreach (var item in calendarDay?.Items ?? [])
            {
                if (TryParseDate(item.AirDate) is { } firstAir && firstAir > weekEnd) continue;
                entries.Add(new TvScheduleEntry(
                    item,
                    date,
                    string.Create(CultureInfo.InvariantCulture, $"{date.Month}/{date.Day}"),
                    IsToday: weekOffset == 0 && date == today));
            }
            sections.Add(new TvScheduleDaySection((TvAirWeekday)weekday, date, IsToday: weekOffset == 0 && date == today, entries));
        }
        return sections;
    }

    public static LibrarySeries? MatchLibrary(IEnumerable<LibrarySeries> series, TvBangumiCalendarItem item) =>
        series.FirstOrDefault(candidate => candidate.ExternalIds.Any(id =>
            id.Provider.Equals("Bangumi", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var idValue) &&
            idValue == item.Id));

    private static DateOnly? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return null;
        return date;
    }

    private (TvBangumiCalendarData? Data, DateTimeOffset SavedAt) ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return (null, default);
            using var document = JsonDocument.Parse(File.ReadAllText(_cachePath));
            if (!document.RootElement.TryGetProperty("savedAtUtc", out var savedElement) ||
                !savedElement.TryGetDateTimeOffset(out var savedAt) ||
                !document.RootElement.TryGetProperty("payload", out var payloadElement) ||
                payloadElement.GetString() is not { Length: > 0 } payload)
                return (null, default);
            var data = ParseCalendar(payload);
            return data.Days.Count == 0 ? (null, default) : (data, savedAt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return (null, default);
        }
    }

    private void WriteCache(string payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var entry = new { savedAtUtc = _clock(), payload };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(entry));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Cache is an optimization; failures must not break the schedule.
        }
    }

    public void Dispose()
    {
        if (_disposeHttp) _http.Dispose();
    }
}