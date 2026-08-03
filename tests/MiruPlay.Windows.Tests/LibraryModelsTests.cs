using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Tests;

public sealed class LibraryModelsTests
{
    [Fact]
    public void SeriesGroupsEpisodesBySeasonAndVersionWithoutLosingOrder()
    {
        var series = new LibrarySeries(
            1,
            "series",
            "Frieren",
            null,
            "",
            2024,
            null,
            ["Fantasy", "Drama"],
            null,
            [
                Episode("s2-e1", 2, 1, "Season 2 01.mkv"),
                Episode("s1-e1-b", 1, 1, "1080p.mkv"),
                Episode("s1-e1-a", 1, 1, "720p.mkv"),
                Episode("s1-e2", 1, 2, "02.mkv"),
            ],
            [new LibraryExtra(8, 1, 1, 1, "PV", "pv.mkv", TimeSpan.FromMinutes(2))]);

        Assert.Equal([1, 2], series.Seasons.Select(season => season.Number));
        Assert.Equal([1, 2], series.Seasons[0].Groups.Select(group => group.Number));
        Assert.True(series.Seasons[0].Groups[0].HasVersions);
        Assert.Equal(["1080p.mkv", "720p.mkv"], series.Seasons[0].Groups[0].Versions.Select(item => item.MediaPath));
        Assert.Equal("2 个版本", series.Seasons[0].Groups[0].VersionText);
        Assert.True(series.HasExtras);
        Assert.Equal("PV", series.Extras[0].DisplayTitle);
    }

    [Fact]
    public void SeriesRecentAndCompletionMetadataUsesEpisodeProgress()
    {
        var watched = Episode("watched", 1, 1, "01.mkv") with
        {
            WatchedPositionMs = 9_000,
            WatchedDurationMs = 10_000,
            LastWatchedEpochMs = 42,
        };
        var inProgress = Episode("progress", 1, 2, "02.mkv") with
        {
            WatchedPositionMs = 2_000,
            WatchedDurationMs = 10_000,
            LastWatchedEpochMs = 84,
        };
        var series = new LibrarySeries(1, "series", "Test", null, "", null, null, [], null, [watched, inProgress], []);

        Assert.Equal(84, series.LastWatchedEpochMs);
        Assert.Equal(1, series.CompletedEpisodeCount);
        Assert.Equal("1/2 集已看", series.CompletionText);
        Assert.Equal("继续", inProgress.PlayActionLabel);
    }

    private static LibraryEpisode Episode(string id, int season, double number, string path) =>
        new(id.GetHashCode(), id, id, season, number, number, "", path, TimeSpan.Zero, []);
}
