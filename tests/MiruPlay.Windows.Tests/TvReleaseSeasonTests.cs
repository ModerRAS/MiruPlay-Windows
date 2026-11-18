using MiruPlay.Windows.Models;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvReleaseSeasonTests
{
    private static LibrarySeries Series(long id, string title, string? airDate) => new(
        id,
        $"uuid-{id}",
        title,
        null,
        string.Empty,
        null,
        airDate,
        [],
        null,
        [],
        []);

    [Theory]
    [InlineData("2026-01-15", 2026, 1)]
    [InlineData("2026-03-01", 2026, 1)]
    [InlineData("2026-04-02", 2026, 4)]
    [InlineData("2026-06-30", 2026, 4)]
    [InlineData("2026-07-01", 2026, 7)]
    [InlineData("2026-08-31", 2026, 7)]
    [InlineData("2026-09-01", 2026, 9)]
    [InlineData("2026-12-31", 2026, 9)]
    [InlineData("2025/04/12", 2025, 4)]
    [InlineData("2025.10", 2025, 9)]
    public void BucketsAirDateIntoBroadcastSeason(string airDate, int expectedYear, int expectedMonth)
    {
        var season = TvReleaseSeason.TryGetSeason(Series(1, "A", airDate));
        Assert.NotNull(season);
        Assert.Equal(expectedYear, season!.Value.Year);
        Assert.Equal(expectedMonth, season.Value.StartMonth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("2026")]
    [InlineData("2026-00-01")]
    [InlineData("2026-13-01")]
    [InlineData("abc 2026 xyz")]
    public void InvalidOrMissingAirDateIsUnknown(string? airDate)
    {
        Assert.Null(TvReleaseSeason.TryGetSeason(Series(1, "A", airDate)));
    }

    [Theory]
    [InlineData("2026-01-05", TvAirWeekday.Monday)]
    [InlineData("2026-01-06", TvAirWeekday.Tuesday)]
    [InlineData("2026-01-07", TvAirWeekday.Wednesday)]
    [InlineData("2026-01-08", TvAirWeekday.Thursday)]
    [InlineData("2026-01-09", TvAirWeekday.Friday)]
    [InlineData("2026-01-10", TvAirWeekday.Saturday)]
    [InlineData("2026-01-11", TvAirWeekday.Sunday)]
    public void DerivesUpdateWeekdayFromAirDate(string airDate, TvAirWeekday expected)
    {
        Assert.Equal(expected, TvReleaseSeason.TryGetWeekday(Series(1, "A", airDate)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-01")]
    [InlineData("2026-01-xx")]
    [InlineData("2026-02-30")]
    public void MissingOrIncompleteDateHasUnknownWeekday(string? airDate)
    {
        Assert.Null(TvReleaseSeason.TryGetWeekday(Series(1, "A", airDate)));
    }

    [Fact]
    public void GroupsAreYearDescendingThenSeasonMonthDescending()
    {
        var groups = TvReleaseSeason.Group([
            Series(1, "A", "2024-09-01"),
            Series(2, "B", "2026-01-01"),
            Series(3, "C", "2026-09-01"),
            Series(4, "D", "2026-07-01"),
            Series(5, "E", "unknown"),
            Series(6, "F", "2025-04-01"),
        ]);

        Assert.Equal([(2026, 9), (2026, 7), (2026, 1), (2025, 4), (2024, 9)], groups
            .Where(group => !group.IsUnknown)
            .Select(group => (group.Year!.Value, group.StartMonth!.Value)));
        Assert.True(groups[^1].IsUnknown);
        Assert.Equal(["E"], groups[^1].Series.Select(item => item.Title));
    }

    [Fact]
    public void WeeksInsideSeasonRunMondayToSundayThenUnknownLast()
    {
        var groups = TvReleaseSeason.Group([
            Series(1, "A", "2026-04-06"),       // Monday
            Series(2, "B", "2026-04-06"),       // Monday
            Series(3, "C", "2026-04-07"),       // Tuesday
            Series(4, "D", "2026-04-12"),       // Sunday
            Series(5, "E", "2026-04-xx"),       // same season, weekday unknown
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(
            [TvAirWeekday.Monday, TvAirWeekday.Tuesday, TvAirWeekday.Sunday, TvAirWeekday.Unknown],
            group.Weeks.Select(week => week.Weekday));
        Assert.Equal(["A", "B"], group.Weeks[0].Series.Select(item => item.Title));
        Assert.Equal(["E"], group.Weeks[^1].Series.Select(item => item.Title));
    }

    [Fact]
    public void TitlesWithinWeekdayAreAscending()
    {
        var group = Assert.Single(TvReleaseSeason.Group([
            Series(1, "Zeta", "2026-07-06"),
            Series(2, "alpha", "2026-07-06"),
            Series(3, "Beta", "2026-07-06"),
            Series(4, "gamma", "2026-07-06"),
        ]));

        Assert.Equal(["alpha", "Beta", "gamma", "Zeta"], group.Weeks[0].Series.Select(item => item.Title));
    }

    [Fact]
    public void UnknownSeasonIsLastAndAllSeriesAppearOnce()
    {
        var groups = TvReleaseSeason.Group([
            Series(1, "Zulu", null),
            Series(2, "Alpha", "not a date"),
            Series(3, "mike", "2026"),
            Series(4, "Known", "2026-10-06"),
        ]);

        Assert.True(groups[^1].IsUnknown);
        Assert.Equal(["Alpha", "mike", "Zulu"], groups[^1].Series.Select(item => item.Title));
        Assert.Equal(4, groups.Sum(group => group.Count));
    }

    [Fact]
    public void EmptyLibraryProducesNoGroups() => Assert.Empty(TvReleaseSeason.Group([]));
}
