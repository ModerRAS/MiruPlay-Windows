using System.Globalization;
using System.Text.RegularExpressions;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>Update weekday for a release date; Unknown = date missing or not a full valid date.</summary>
public enum TvAirWeekday
{
    Unknown = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 3,
    Thursday = 4,
    Friday = 5,
    Saturday = 6,
    Sunday = 7,
}

/// <summary>One weekday row inside a broadcast season.</summary>
public sealed record TvWeekdayGroup(TvAirWeekday Weekday, IReadOnlyList<LibrarySeries> Series)
{
    public string Title => AppStrings.Get(Weekday switch
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

/// <summary>One broadcast-season bucket on the TV season wall (year + 1/4/7/9 start month).</summary>
public sealed record TvSeasonGroup(int? Year, int? StartMonth, IReadOnlyList<TvWeekdayGroup> Weeks)
{
    public bool IsUnknown => Year is null || StartMonth is null;

    public string Title => IsUnknown
        ? AppStrings.Get("tv.season.unknown")
        : AppStrings.Get("tv.season.group", Year!.Value, StartMonth!.Value);

    public IEnumerable<LibrarySeries> Series => Weeks.SelectMany(week => week.Series);

    public int Count => Weeks.Sum(week => week.Series.Count);
}

/// <summary>
/// TV-mode release-season grouping. Mirrors the Android TV semantics without
/// touching the desktop LibraryOrganization behaviour: parse YYYY-MM-DD from
/// AirDate, bucket months into 1/4/7/9 (>=9→9, >=7→7, >=4→4, else→1), sort
/// seasons by year descending then start month descending, and inside each
/// season group by update weekday (Monday..Sunday, unknown weekday last).
/// Titles sort ascending within a weekday; fully unparseable dates form a final
/// "unknown air date" season.
/// </summary>
public static class TvReleaseSeason
{
    private static readonly Regex AirDateParts = new(
        @"([0-9]{4})\D+([0-9]{1,2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FullDateParts = new(
        @"^\s*([0-9]{4})\D+([0-9]{1,2})\D+([0-9]{1,2})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (int Year, int StartMonth)? TryGetSeason(LibrarySeries series)
    {
        var match = AirDateParts.Match(series.AirDate ?? string.Empty);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year <= 0)
            return null;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            month is < 1 or > 12)
            return null;
        return (year, month >= 9 ? 9 : month >= 7 ? 7 : month >= 4 ? 4 : 1);
    }

    /// <summary>Day of week from a complete YYYY-MM-DD release date; null when absent/invalid.</summary>
    public static TvAirWeekday? TryGetWeekday(LibrarySeries series)
    {
        var match = FullDateParts.Match(series.AirDate ?? string.Empty);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            return null;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day is < 1 or > 31) return null;
        try
        {
            var date = new DateOnly(year, month, day);
            return date.DayOfWeek == DayOfWeek.Sunday
                ? TvAirWeekday.Sunday
                : (TvAirWeekday)(int)date.DayOfWeek;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static IReadOnlyList<TvSeasonGroup> Group(IEnumerable<LibrarySeries> series)
    {
        var items = series.ToList();
        var known = items
            .Select(item => (Series: item, Season: TryGetSeason(item)))
            .Where(item => item.Season is not null)
            .GroupBy(item => item.Season!.Value)
            .Select(group => new TvSeasonGroup(
                group.Key.Year,
                group.Key.StartMonth,
                group.GroupBy(item => TryGetWeekday(item.Series) ?? TvAirWeekday.Unknown)
                    .OrderBy(week => week.Key == TvAirWeekday.Unknown ? 8 : (int)week.Key)
                    .Select(week => new TvWeekdayGroup(
                        week.Key,
                        week.Select(item => item.Series)
                            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                            .ToList()))
                    .ToList()))
            .OrderByDescending(group => group.Year)
            .ThenByDescending(group => group.StartMonth)
            .ToList();

        var unknown = items
            .Where(item => TryGetSeason(item) is null)
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
        {
            known.Add(new TvSeasonGroup(
                null,
                null,
                [new TvWeekdayGroup(TvAirWeekday.Unknown, unknown)]));
        }
        return known;
    }
}
