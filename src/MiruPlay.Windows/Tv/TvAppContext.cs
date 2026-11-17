using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

public sealed record TvContinueItem(
    LibrarySeries Series,
    LibraryEpisode Episode,
    string EpisodeLabel,
    double ProgressPercent,
    string RemainingText);

public sealed record TvGenreCard(string Genre, int Count, IReadOnlyList<LibrarySeries> Series);

public sealed record TvHomeData(
    IReadOnlyList<TvContinueItem> ContinueWatching,
    IReadOnlyList<LibrarySeries> RecentlyAdded,
    IReadOnlyList<TvGenreCard> Genres,
    IReadOnlyList<LibrarySeries> AllSeries);

/// <summary>
/// Read-only view of the user's MLIP library for TV mode. It reuses the
/// configured media sources from settings.json, loads the active catalog and
/// applies playback progress; it never adds, scans or removes sources.
/// </summary>
public sealed class TvAppContext : IAsyncDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private readonly PlaybackProgressStore _progressStore;
    private readonly MediaSourceRegistry _mediaSources;

    public TvAppContext(string? settingsPath = null, string? stateDatabasePath = null)
    {
        _settingsStore = new AppSettingsStore(settingsPath);
        _progressStore = new PlaybackProgressStore(stateDatabasePath);
        Settings = _settingsStore.Load();
        _mediaSources = new MediaSourceRegistry(
            () => Settings,
            updated => { Settings = updated; });
    }

    public AppSettings Settings { get; private set; }

    public PlaybackProgressStore ProgressStore => _progressStore;

    public MediaSourceRegistry MediaSources => _mediaSources;

    public MediaSourceDefinition? ActiveSource { get; private set; }

    public IReadOnlyList<LibrarySeries> Catalog { get; private set; } = [];

    public IReadOnlyList<LibrarySeries> AllSeries { get; private set; } = [];

    public TvHomeData Home { get; private set; } = new([], [], [], []);

    public bool IsLoaded => ActiveSource is not null;

    public event EventHandler? LibraryChanged;

    public bool HasSources => Settings.MediaSources is { Count: > 0 };

    public string ContentMode => Settings.CurrentAppMode == "drama" ? "DRAMA" : "ANIME";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var sources = Settings.MediaSources ?? [];
        if (sources.Count == 0)
        {
            ActiveSource = null;
            Catalog = [];
            AllSeries = [];
            Home = new([], [], [], []);
            return;
        }

        var source = (Settings.ActiveSourceId is long activeId ? sources.FirstOrDefault(item => item.Id == activeId) : null)
            ?? sources.FirstOrDefault(item => item.ContentMode.Equals(ContentMode, StringComparison.OrdinalIgnoreCase))
            ?? sources[0];
        ActiveSource = source;
        var catalog = await Task.Run(() => _mediaSources.LoadCatalog(source.Id), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        Catalog = ApplySource(catalog.Series, source.Id);
        Rebuild();
    }

    /// <summary>Re-read progress rows from state.db and rebuild TV rows (called after playback).</summary>
    public void RefreshProgress()
    {
        if (Catalog.Count == 0) return;
        Rebuild();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() => RefreshProgress();

    public void ReloadSettings() => Settings = _settingsStore.Load();

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        _settingsStore.Save(settings);
    }

    public IReadOnlyList<LibrarySeries> FilterLibrary(string? query, string? genre = null)
    {
        IEnumerable<LibrarySeries> result = AllSeries;
        if (!string.IsNullOrWhiteSpace(genre))
        {
            result = result.Where(series => series.Genres.Any(item =>
                item.Equals(genre, StringComparison.OrdinalIgnoreCase)));
        }
        var text = query?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            result = result.Where(series =>
                series.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                (series.OriginalTitle?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                series.Genres.Any(item => item.Contains(text, StringComparison.CurrentCultureIgnoreCase)));
        }
        return result.ToList();
    }

    public LibrarySeries? FindSeries(long seriesId) => AllSeries.FirstOrDefault(series => series.Id == seriesId);

    public static long SeriesIdOf(LibrarySeries series) => series.Id;

    /// <summary>Progress for a series card: the furthest along in-progress episode, else completed count.</summary>
    public static (double Percent, string Label, bool Completed) SeriesProgress(LibrarySeries series)
    {
        var inProgress = series.Episodes.Where(episode => episode.IsInProgress)
            .OrderByDescending(episode => episode.LastWatchedEpochMs)
            .FirstOrDefault();
        if (inProgress is not null)
            return (inProgress.ProgressPercent, inProgress.DisplayNumber, false);
        if (series.Episodes.Count == 0) return (0, string.Empty, false);
        return (0, AppStrings.Get("tv.card.watched", series.CompletedEpisodeCount, series.Episodes.Count),
            series.Episodes.All(episode => episode.IsCompleted));
    }

    public static LibraryEpisode? ResumeEpisode(LibrarySeries series) =>
        series.Episodes.Where(episode => episode.IsInProgress)
            .OrderByDescending(episode => episode.LastWatchedEpochMs)
            .ThenBy(episode => episode.Season)
            .ThenBy(episode => episode.Number)
            .FirstOrDefault()
        ?? series.Episodes.Where(episode => !episode.IsCompleted)
            .OrderBy(episode => episode.Season)
            .ThenBy(episode => episode.Number)
            .FirstOrDefault()
        ?? series.Episodes.OrderBy(episode => episode.Season).ThenBy(episode => episode.Number).FirstOrDefault();

    public static LibraryEpisode? FirstEpisode(LibrarySeries series) => series.Episodes
        .OrderBy(episode => episode.Season)
        .ThenBy(episode => episode.Number)
        .ThenBy(episode => episode.MediaPath, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    public async ValueTask DisposeAsync()
    {
        _mediaSources.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static List<LibrarySeries> ApplySource(IReadOnlyList<LibrarySeries> series, long sourceId) =>
        series.Select(item => item with
        {
            Episodes = item.Episodes.Select(episode => episode with { SourceId = sourceId }).ToList(),
        }).ToList();

    private void Rebuild()
    {
        var progress = _progressStore.GetAll();
        Catalog = Catalog.Select(item => item with
        {
            Episodes = item.Episodes.Select(episode =>
            {
                var tracked = progress.GetValueOrDefault(episode.ProgressKey);
                return tracked is null ? episode : episode with
                {
                    WatchedPositionMs = tracked.PositionMs,
                    WatchedDurationMs = tracked.DurationMs,
                    LastWatchedEpochMs = tracked.LastWatchedEpochMs,
                    PlayCount = tracked.PlayCount,
                };
            }).ToList(),
        }).ToList();
        AllSeries = LibraryOrganization.Apply(Catalog, Settings);
        Home = BuildHome(AllSeries);
    }

    private static TvHomeData BuildHome(IReadOnlyList<LibrarySeries> series)
    {
        var continueWatching = series
            .SelectMany(item => item.Episodes
                .Where(episode => episode.IsInProgress)
                .Select(episode => new TvContinueItem(
                    item,
                    episode,
                    $"{episode.DisplayNumber} · {episode.DisplayTitle}",
                    episode.ProgressPercent,
                    RemainingText(episode))))
            .GroupBy(item => item.Series.Uuid, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Episode.LastWatchedEpochMs).First())
            .OrderByDescending(item => item.Episode.LastWatchedEpochMs)
            .Take(12)
            .ToList();

        var recentlyAdded = series
            .OrderByDescending(item => item.AirDate ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(item => item.Year ?? 0)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(18)
            .ToList();

        var genres = series
            .SelectMany(item => item.Genres.DefaultIfEmpty(AppStrings.Get("tv.genre.unknown")), (item, genre) => new { item, genre })
            .GroupBy(pair => pair.genre, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(10)
            .Select(group => new TvGenreCard(group.Key, group.Count(), group.Select(pair => pair.item).ToList()))
            .ToList();

        return new TvHomeData(continueWatching, recentlyAdded, genres, series.ToList());
    }

    private static string RemainingText(LibraryEpisode episode)
    {
        var remaining = episode.WatchedDurationMs - episode.WatchedPositionMs;
        if (remaining <= 0) return string.Empty;
        var span = TimeSpan.FromMilliseconds(remaining);
        return span.TotalHours >= 1
            ? AppStrings.Get("tv.card.remainingHm", (int)span.TotalHours, span.Minutes)
            : AppStrings.Get("tv.card.remainingM", Math.Max(1, span.Minutes));
    }
}

/// <summary>Shared value formatting for TV cards.</summary>
public static class TvPresentation
{
    public static string MetadataLine(LibrarySeries series) => string.Join("  ·  ", new[]
    {
        series.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        series.Genres.Count > 0 ? series.Genres[0] : null,
        AppStrings.Get("tv.card.episodes", series.Episodes.Count),
        series.CompletionText is { Length: > 0 } completion ? completion : null,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public static string SeasonLine(IReadOnlyList<LibraryEpisode> episodes)
    {
        var seasons = episodes.Select(episode => Math.Max(1, episode.Season)).Distinct().Order().ToList();
        return seasons.Count <= 1
            ? AppStrings.Get("lib.season1")
            : AppStrings.Get("lib.seasonN", seasons.Count);
    }

    public static string EpisodeProgressText(LibraryEpisode episode) =>
        episode.IsCompleted
            ? AppStrings.Get("tv.detail.watched")
            : episode.IsInProgress
                ? AppStrings.Get("tv.detail.progress", episode.ProgressPercent.ToString("0", System.Globalization.CultureInfo.CurrentCulture))
                : string.Empty;

    public static string FormatTime(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    public static string FormatSpeed(double speed) =>
        speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
}
