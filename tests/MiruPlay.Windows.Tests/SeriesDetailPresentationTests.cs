using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class SeriesDetailPresentationTests
{
    [Fact]
    public void BuildEpisodeOptionsKeepsAllVersionsInEpisodeOrder()
    {
        var series = TestSeriesWithEpisodes(
            Episode("web", 1, 2, @"web\02.mkv"),
            Episode("s1", 1, 1, @"web\01.mkv"),
            Episode("bd", 1, 2, @"bd\02.mkv"));

        var options = SeriesDetailPresentation.BuildEpisodeOptions(series);

        Assert.Equal("s1", options[0].Episode.ProgressKey);
        Assert.Equal("bd", options[1].Episode.ProgressKey);
        Assert.Equal("web", options[2].Episode.ProgressKey);
        Assert.Equal("第 2 集 · 02", options[2].DisplayLabel);
        Assert.Equal("2 个版本", options[1].VersionText);
    }

    [Fact]
    public void BuildViewStateGroupsSeasonsVersionsExtrasAndStartsANewSeries()
    {
        var series = TestSeriesWithEpisodes(
            Episode("s2", 2, 1, @"s2\01.mkv"),
            Episode("web", 1, 1, @"web\01.mkv"),
            Episode("bd", 1, 1, @"bd\01.mkv")) with
        {
            Extras = [new LibraryExtra(9, 1, 2, 20, "制作花絮", "extra-b.mkv", TimeSpan.FromMinutes(3)),
                new LibraryExtra(8, 1, 1, 10, "无字幕片头", "extra-a.mkv", TimeSpan.FromMinutes(2))],
        };

        var state = SeriesDetailPresentation.BuildViewState(series);

        Assert.Equal([1, 2], state.Seasons.Select(season => season.Number));
        Assert.Equal(2, state.Seasons[0].Episodes[0].Versions.Count);
        Assert.Equal("bd", state.Seasons[0].Episodes[0].Versions[0].Episode.ProgressKey);
        Assert.Equal(["无字幕片头", "制作花絮"], state.Extras.Select(extra => extra.Extra.DisplayTitle));
        Assert.Equal("播放第一集", state.PrimaryAction!.Label);
        Assert.Equal("bd", state.PrimaryAction.Episode.ProgressKey);
        Assert.Null(state.EmptyStateText);
    }

    [Fact]
    public void PrimaryActionUsesMostRecentProgressThenReplayAfterAllLogicalEpisodesComplete()
    {
        var older = Episode("older", 1, 1, @"web\01.mkv") with
        {
            WatchedPositionMs = 120_000,
            WatchedDurationMs = 1_440_000,
            LastWatchedEpochMs = 10,
        };
        var newer = Episode("newer", 1, 2, @"web\02.mkv") with
        {
            WatchedPositionMs = 240_000,
            WatchedDurationMs = 1_440_000,
            LastWatchedEpochMs = 20,
        };

        var inProgress = SeriesDetailPresentation.BuildViewState(TestSeriesWithEpisodes(older, newer));
        Assert.Equal("继续观看", inProgress.PrimaryAction!.Label);
        Assert.Equal("newer", inProgress.PrimaryAction.Episode.ProgressKey);

        var completed = SeriesDetailPresentation.BuildViewState(TestSeriesWithEpisodes(
            older with { WatchedPositionMs = 1_440_000 },
            newer with { WatchedPositionMs = 1_440_000 }));
        Assert.Equal("重新观看", completed.PrimaryAction!.Label);
        Assert.Equal("older", completed.PrimaryAction.Episode.ProgressKey);
    }

    [Fact]
    public void EmptySeriesHasExplicitEmptyStateAndNoPrimaryAction()
    {
        var state = SeriesDetailPresentation.BuildViewState(TestSeriesWithEpisodes());

        Assert.Null(state.PrimaryAction);
        Assert.Equal("这个系列暂时没有可播放的剧集。", state.EmptyStateText);
        Assert.Empty(state.Seasons);
    }

    private static LibrarySeries TestSeriesWithEpisodes(params LibraryEpisode[] episodes) => new(
        1,
        "series",
        "Series",
        null,
        "Summary",
        2026,
        null,
        [],
        null,
        episodes,
        []);

    private static LibraryEpisode Episode(string key, int season, double number, string path) => new(
        1,
        key,
        key,
        season,
        number,
        number,
        key,
        path,
        TimeSpan.FromMinutes(24),
        []);
}
