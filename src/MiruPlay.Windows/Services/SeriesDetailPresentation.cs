using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record SeriesDetailEpisodeOption(
    LibraryEpisode Episode,
    string DisplayLabel,
    double ProgressPercent,
    string ProgressText,
    string VersionText,
    bool IsCompleted,
    string AccessibleName);

public sealed record SeriesDetailVersionViewState(
    LibraryEpisode Episode,
    string DisplayLabel,
    string ProgressText,
    double ProgressPercent,
    bool IsDefault,
    bool IsCompleted,
    string AccessibleName);

public sealed record SeriesDetailEpisodeViewState(
    int Season,
    double Number,
    string DisplayNumber,
    string DisplayTitle,
    IReadOnlyList<SeriesDetailVersionViewState> Versions,
    bool IsCompleted,
    string AccessibleName)
{
    public SeriesDetailVersionViewState DefaultVersion => Versions[0];
    public bool HasMultipleVersions => Versions.Count > 1;
}

public sealed record SeriesDetailSeasonViewState(
    int Number,
    string DisplayName,
    IReadOnlyList<SeriesDetailEpisodeViewState> Episodes,
    int EpisodeCount,
    int VersionCount);

public sealed record SeriesDetailExtraViewState(
    LibraryExtra Extra,
    string DisplayLabel,
    string AccessibleName);

public sealed record SeriesDetailPrimaryAction(
    LibraryEpisode Episode,
    string Label,
    string AccessibleName);

public sealed record SeriesDetailViewState(
    LibrarySeries Series,
    IReadOnlyList<SeriesDetailSeasonViewState> Seasons,
    IReadOnlyList<SeriesDetailExtraViewState> Extras,
    SeriesDetailPrimaryAction? PrimaryAction,
    string? EmptyStateText);

public abstract record SeriesPlayTarget;

public sealed record SeriesEpisodePlayTarget(LibraryEpisode Episode) : SeriesPlayTarget;

public sealed record SeriesExtraPlayTarget(LibraryExtra Extra) : SeriesPlayTarget;

public static class SeriesDetailPresentation
{
    public static SeriesDetailViewState BuildViewState(LibrarySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var seasons = series.Seasons
            .Select(season => new SeriesDetailSeasonViewState(
                season.Number,
                season.DisplayName,
                season.Groups.Select(group => new SeriesDetailEpisodeViewState(
                    season.Number,
                    group.Number,
                    group.DisplayNumber,
                    group.DefaultEpisode.DisplayTitle,
                    group.Versions.Select((episode, index) => new SeriesDetailVersionViewState(
                        episode,
                        episode.VersionLabel,
                        episode.ProgressText,
                        episode.ProgressPercent,
                        index == 0,
                        episode.IsCompleted,
                        BuildAccessibleName(season.Number, group.Number, episode))).ToList(),
                    group.Versions.Any(episode => episode.IsCompleted),
                    $"{season.DisplayName} {group.DisplayNumber} {group.DefaultEpisode.DisplayTitle}"))
                    .ToList(),
                season.EpisodeCount,
                season.VersionCount))
            .ToList();

        var episodes = seasons.SelectMany(season => season.Episodes).ToList();
        var allEpisodes = episodes.SelectMany(episode => episode.Versions.Select(version => version.Episode)).ToList();
        var primaryEpisode = allEpisodes
            .Where(episode => episode.IsInProgress)
            .OrderByDescending(episode => episode.LastWatchedEpochMs)
            .ThenBy(episode => episode.Season)
            .ThenBy(episode => episode.Number)
            .ThenBy(episode => episode.MediaPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var allLogicalEpisodesCompleted = episodes.Count > 0 && episodes.All(episode => episode.IsCompleted);
        var hasHistory = allEpisodes.Any(episode => episode.IsCompleted || episode.IsInProgress || episode.PlayCount > 0);
        var firstUncompleted = episodes.FirstOrDefault(episode => !episode.IsCompleted);
        if (primaryEpisode is null)
            primaryEpisode = allLogicalEpisodesCompleted
                ? episodes.FirstOrDefault()?.DefaultVersion.Episode
                : (firstUncompleted ?? episodes.FirstOrDefault())?.DefaultVersion.Episode;

        var primaryLabel = primaryEpisode is null
            ? null
            : allLogicalEpisodesCompleted
                ? AppStrings.Get("detail.rewatch")
                : primaryEpisode.IsInProgress || hasHistory
                    ? AppStrings.Get("detail.continueWatch")
                    : AppStrings.Get("detail.playFirst");

        var extras = series.Extras
            .OrderBy(extra => extra.SortOrder)
            .ThenBy(extra => extra.Ordinal)
            .ThenBy(extra => extra.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(extra => new SeriesDetailExtraViewState(
                extra,
                extra.DisplayTitle,
                AppStrings.Get("detail.playExtra2", extra.DisplayTitle)))
            .ToList();

        return new(
            series,
            seasons,
            extras,
            primaryEpisode is null || primaryLabel is null
                ? null
                : new(primaryEpisode, primaryLabel, $"{primaryLabel} {primaryEpisode.DisplayNumber} {primaryEpisode.DisplayTitle}"),
            episodes.Count == 0 ? AppStrings.Get("detail.noEpisodes") : null);
    }

    public static IReadOnlyList<SeriesDetailEpisodeOption> BuildEpisodeOptions(LibrarySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        return series.Seasons
            .SelectMany(season => season.Groups)
            .SelectMany(group => group.Versions.Select(episode => new SeriesDetailEpisodeOption(
                episode,
                $"{episode.DisplayNumber} · {episode.VersionLabel}",
                episode.ProgressPercent,
                episode.ProgressText,
                group.VersionText,
                episode.IsCompleted,
                $"{episode.PlayActionLabel} {episode.DisplayNumber} {episode.VersionLabel}")))
            .ToList();
    }

    private static string BuildAccessibleName(int season, double number, LibraryEpisode episode) =>
        (season <= 1 ? AppStrings.Get("lib.season1") : AppStrings.Get("lib.seasonN", season)) + " " + AppStrings.Get("detail.accessName", number, episode.DisplayTitle, episode.VersionLabel) + (episode.IsInProgress ? AppStrings.Get("detail.continueWatch") : episode.IsCompleted ? AppStrings.Get("detail.watchedUp") : AppStrings.Get("detail.unwatched"));
}
