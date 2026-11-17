using MiruPlay.Windows.Models;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvPresentationTests
{
    private static LibraryEpisode Episode(long id, int season, double number, long watchedMs, long durationMs, int playCount = 0) =>
        new(id, $"episode-{id}", $"progress-{id}", season, number, number, $"Episode {number}", $"C:/media/{id}.mkv",
            TimeSpan.FromMinutes(24), [])
        {
            WatchedPositionMs = watchedMs,
            WatchedDurationMs = durationMs,
            LastWatchedEpochMs = id,
            PlayCount = playCount,
        };

    private static LibrarySeries Series(params LibraryEpisode[] episodes) =>
        new(1, "series-1", "Test series", null, string.Empty, 2026, "2026-01-01", ["Action"], "C:/poster.jpg", episodes, []);

    [Fact]
    public void SeriesProgressUsesTheLatestInProgressEpisode()
    {
        var series = Series(
            Episode(1, 1, 1, 900_000, 1_440_000, 1),
            Episode(2, 1, 2, 300_000, 1_440_000, 1));
        var (percent, label, completed) = TvAppContext.SeriesProgress(series);
        Assert.Equal(2, series.Episodes.Count(episode => episode.IsInProgress));
        Assert.InRange(percent, 20, 21);
        Assert.False(completed);
        Assert.Equal("第 2 集", label);
    }

    [Fact]
    public void ResumeEpisodeFallsBackToFirstUnwatchedThenFirstEpisode()
    {
        var inProgress = Series(Episode(1, 1, 1, 60_000, 1_440_000), Episode(2, 1, 2, 0, 1_440_000));
        Assert.Equal(1, TvAppContext.ResumeEpisode(inProgress)?.Id);

        var unwatched = Series(Episode(3, 1, 1, 1_440_000, 1_440_000, 1), Episode(4, 1, 2, 0, 1_440_000));
        Assert.Equal(4, TvAppContext.ResumeEpisode(unwatched)?.Id);
        Assert.Equal(3, TvAppContext.FirstEpisode(unwatched)?.Id);
        Assert.Null(TvAppContext.FirstEpisode(Series()));
    }

    [Fact]
    public void EpisodeLabelFormattingMatchesTheDesignSpec()
    {
        Assert.Equal("0:00", TvPresentation.FormatTime(0));
        Assert.Equal("1:05", TvPresentation.FormatTime(65_000));
        Assert.Equal("1:01:05", TvPresentation.FormatTime(3_665_000));
        Assert.Equal("1.5x", TvPresentation.FormatSpeed(1.5));
        Assert.Equal("1x", TvPresentation.FormatSpeed(1));
    }

    [Fact]
    public void MetadataLineCombinesYearGenreEpisodeCountAndWatchedState()
    {
        var series = Series(Episode(1, 1, 1, 1_440_000, 1_440_000, 1));
        var line = TvPresentation.MetadataLine(series);
        Assert.Contains("2026", line);
        Assert.Contains("Action", line);
        Assert.Contains("1", line);
    }
}
