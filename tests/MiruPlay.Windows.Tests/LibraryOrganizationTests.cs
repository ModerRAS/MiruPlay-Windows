using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class LibraryOrganizationTests
{
    [Fact]
    public void MergeKeepsStableIdsAllVersionsProgressAndOriginalCatalog()
    {
        var first = Series("first", "A.Show", "2025-04-01") with
        {
            ExternalIds = [new("Bangumi", "42")],
            Episodes = [Episode("a", 1) with { SourceId = 7, WatchedPositionMs = 5_000 }],
        };
        var second = Series("second", "a_show", "2025-04-01") with
        {
            Summary = "Full metadata",
            PosterPath = "poster.jpg",
            ExternalIds = [new("Bangumi", "42")],
            Episodes = [Episode("b", 1) with { SourceId = 8 }],
        };
        var third = Series("third", "Another title", "2025-04-01") with { ExternalIds = [new("Bangumi", "42")] };
        LibrarySeries[] original = [first, second, third];
        var merged = Assert.Single(LibraryOrganization.Apply(original, new(MergeSameAnimeEnabled: true)));

        Assert.Equal(first.ApiId, merged.ApiId);
        Assert.Equal([second.ApiId, third.ApiId], merged.AlternateApiIds);
        Assert.Equal("Full metadata", merged.Summary);
        Assert.Equal(["a", "b", "third"], merged.Episodes.Select(item => item.ProgressKey));
        Assert.Equal([7L, 8L, 0L], merged.Episodes.Select(item => item.SourceId));
        Assert.Equal(5_000, merged.Episodes[0].WatchedPositionMs);
        Assert.Equal(3, merged.Seasons[0].Groups[0].Versions.Count);
        Assert.Equal(3, LibraryOrganization.Apply(original, new()).Count);
        Assert.Single(first.Episodes);
        Assert.Empty(first.AlternateApiIds);
    }

    [Fact]
    public void MissingIdsCannotBridgeConflictingProviderIdentities()
    {
        var first = Series("one", "Show", null) with { ExternalIds = [new("Bangumi", "1")] };
        var unknown = Series("unknown", "show", null);
        var conflicting = Series("two", "SHOW", null) with { ExternalIds = [new("Bangumi", "2")] };
        var result = LibraryOrganization.Apply([first, unknown, conflicting], new(MergeSameAnimeEnabled: true));

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0].Episodes.Count);
        Assert.Equal("2", result[1].ExternalIds[0].Value);
        Assert.Equal(2, LibraryOrganization.Apply(
            [Series("empty", "", null), Series("unknown-title", "unknown", null)],
            new(MergeSameAnimeEnabled: true)).Count);
    }

    [Fact]
    public void ReleaseSeasonSortUsesAirDateNewestFirstAndUnknownLast()
    {
        var series = new[]
        {
            Series("year-only", "A", "2026"),
            Series("spring", "C", "2026-04-01"),
            Series("autumn", "B", "2026-09-30"),
            Series("winter", "D", "2025-02-01"),
        };
        Assert.Equal(["autumn", "spring", "winter", "year-only"],
            LibraryOrganization.Apply(series, new(PosterWallArrangement: "RELEASE_SEASON")).Select(item => item.Uuid));
        Assert.Equal(["year-only", "autumn", "spring", "winter"],
            LibraryOrganization.Apply(series, new()).Select(item => item.Uuid));
        Assert.Equal("2026年9月新番", LibraryOrganization.ReleaseSeasonTitle(series[2]));
        Assert.Equal("未识别播出日期", LibraryOrganization.ReleaseSeasonTitle(series[0]));
    }

    [Theory]
    [InlineData("2026-01-01", 202601)]
    [InlineData("2026/4/1", 202604)]
    [InlineData("2026-08-31", 202607)]
    [InlineData("2026-09-01", 202609)]
    [InlineData("2026-12-31", 202609)]
    [InlineData("2026-00-01", null)]
    [InlineData("2026-13-01", null)]
    [InlineData("2026", null)]
    [InlineData(null, null)]
    public void ReleaseSeasonMatchesAndroidMonthBuckets(string? airDate, int? expected) =>
        Assert.Equal(expected, LibraryOrganization.ReleaseSeasonKey(Series("series", "Show", airDate)));

    private static LibrarySeries Series(string id, string title, string? airDate) =>
        new(1, id, title, null, "", 2026, airDate, [], null, [Episode(id, 1)], []);

    private static LibraryEpisode Episode(string id, int number) =>
        new(1, id, id, 1, number, number, id, id + ".mkv", TimeSpan.Zero, []);
}
