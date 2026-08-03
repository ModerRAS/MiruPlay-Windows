namespace MiruPlay.Windows.Models;

public sealed record ExternalMetadataId(string Provider, string Value)
{
    public int? NumericValue => int.TryParse(Value, out var value) && value > 0 ? value : null;
    public string DisplayLabel => $"{Provider} {Value}";
    public Uri? Link => (Provider, NumericValue) switch
    {
        ("Bangumi", int id) => new Uri($"https://bgm.tv/subject/{id}"),
        ("TMDB", int id) => new Uri($"https://www.themoviedb.org/tv/{id}"),
        ("AniDB", int id) => new Uri($"https://anidb.net/anime/{id}"),
        _ => null,
    };
}

public sealed record MlipArtworkPack(
    long Id,
    string Path,
    string Sha256,
    long ByteSize,
    int EntryCount,
    IReadOnlyList<MlipArtworkAsset> Assets);

public sealed record MlipArtworkAsset(
    long Id,
    string Sha256,
    long PackId,
    string MemberName,
    long DataOffset,
    long DataLength,
    string MediaType,
    int? Width,
    int? Height)
{
    public string Extension => Path.GetExtension(MemberName).ToLowerInvariant();
}

public sealed record MlipArtworkReference(
    MlipArtworkAsset Asset,
    string? LegacyPath,
    int? SourceProvider,
    string? SourceSubjectId,
    string? SourceUrl,
    string? DownloadedAt);

public sealed record MlipArtworkBinding(
    string OwnerKind,
    long OwnerId,
    int ArtworkKind,
    string? LegacyPath,
    MlipArtworkReference? Reference);

public sealed record LibraryCatalog(int SchemaVersion, string RootPath, IReadOnlyList<LibrarySeries> Series)
{
    public IReadOnlyList<MlipArtworkPack> ArtworkPacks { get; init; } = [];
    public IReadOnlyList<MlipArtworkBinding> ArtworkBindings { get; init; } = [];
}

public sealed record LibraryEpisodeGroup(
    double Number,
    IReadOnlyList<LibraryEpisode> Versions)
{
    public LibraryEpisode DefaultEpisode => Versions[0];
    public string DisplayNumber => Number % 1 == 0 ? $"第 {Number:0} 集" : $"第 {Number:0.##} 集";
    public bool HasVersions => Versions.Count > 1;
    public string VersionText => HasVersions ? $"{Versions.Count} 个版本" : string.Empty;
}

public sealed record LibrarySeason(
    int Number,
    IReadOnlyList<LibraryEpisodeGroup> Groups)
{
    public string DisplayName => Number <= 1 ? "第 1 季" : $"第 {Number} 季";
    public int EpisodeCount => Groups.Count;
    public int VersionCount => Groups.Sum(group => group.Versions.Count);
}

public sealed record LibraryGenreGroup(string Name, IReadOnlyList<LibrarySeries> Series)
{
    public string DisplayName => $"{Name} · {Series.Count}";
}

public sealed record LibrarySeries(
    long Id,
    string Uuid,
    string Title,
    string? OriginalTitle,
    string Summary,
    int? Year,
    string? AirDate,
    IReadOnlyList<string> Genres,
    string? PosterPath,
    IReadOnlyList<LibraryEpisode> Episodes,
    IReadOnlyList<LibraryExtra> Extras)
{
    public IReadOnlyList<ExternalMetadataId> ExternalIds { get; init; } = [];
    public MlipArtworkReference? PosterArtwork { get; init; }
    public string ApiId => $"mlip:windows:{Uuid}";
    public ExternalMetadataId? ExternalId(string provider) =>
        ExternalIds.FirstOrDefault(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<ExternalMetadataId> ExternalLinks => ExternalIds.Where(item => item.Link is not null).ToList();
    public IReadOnlyList<LibrarySeason> Seasons => Episodes
        .GroupBy(episode => Math.Max(1, episode.Season))
        .OrderBy(group => group.Key)
        .Select(group => new LibrarySeason(
            group.Key,
            group.GroupBy(episode => episode.Number)
                .OrderBy(episodeGroup => episodeGroup.Key)
                .Select(episodeGroup => new LibraryEpisodeGroup(
                    episodeGroup.Key,
                    episodeGroup.OrderBy(episode => episode.MediaPath, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList()))
        .ToList();
    public IReadOnlyList<LibraryEpisode> OrderedEpisodes => Seasons.SelectMany(season => season.Groups.SelectMany(group => group.Versions)).ToList();
    public bool HasMultipleVersions => Seasons.Any(season => season.Groups.Any(group => group.HasVersions));
    public bool HasExtras => Extras.Count > 0;
    public long LastWatchedEpochMs => Episodes.Max(episode => episode.LastWatchedEpochMs);
    public int CompletedEpisodeCount => Episodes.Count(episode => episode.IsCompleted);
    public string CompletionText => Episodes.Count == 0 ? string.Empty : $"{CompletedEpisodeCount}/{Episodes.Count} 集已看";
    public Uri? PosterUri => PosterPath switch
    {
        null => null,
        _ when Uri.TryCreate(PosterPath, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" => uri,
        _ when File.Exists(PosterPath) => new Uri(PosterPath),
        _ => null,
    };
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "M" : Title[..1].ToUpperInvariant();
    public string MetadataLine => string.Join("  ·  ", new[]
    {
        AirDate ?? Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Genres.Count > 0 ? Genres[0] : null,
        $"{Episodes.Count} 集",
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record LibraryEpisode(
    long Id,
    string Uuid,
    string ProgressKey,
    int Season,
    double Number,
    double SortOrder,
    string Title,
    string MediaPath,
    TimeSpan Duration,
    IReadOnlyList<string> SubtitlePaths)
{
    public IReadOnlyList<ExternalMetadataId> ExternalIds { get; init; } = [];
    public long SourceId { get; init; }
    public ExternalMetadataId? ExternalId(string provider) =>
        ExternalIds.FirstOrDefault(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
    public long WatchedPositionMs { get; init; }
    public long WatchedDurationMs { get; init; }
    public long LastWatchedEpochMs { get; init; }
    public int PlayCount { get; init; }

    public string ApiId => ProgressKey;
    public string FileName => Path.GetFileName(MediaPath);
    public string DisplayNumber => Number % 1 == 0 ? $"第 {Number:0} 集" : $"第 {Number:0.##} 集";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(MediaPath) : Title;
    public string VersionLabel => Path.GetFileNameWithoutExtension(MediaPath);
    public string DurationText => Duration <= TimeSpan.Zero ? string.Empty : $"{(int)Duration.TotalMinutes} 分钟";
    public bool IsCompleted => WatchedDurationMs > 0 && WatchedPositionMs >= WatchedDurationMs * 0.9 || WatchedDurationMs == 0 && PlayCount > 0;
    public bool IsInProgress => WatchedPositionMs > 0 && !IsCompleted;
    public double ProgressPercent => WatchedDurationMs > 0 ? Math.Clamp(WatchedPositionMs * 100d / WatchedDurationMs, 0, 100) : 0;
    public string PlayActionLabel => IsInProgress ? "继续" : "播放";
    public string ProgressText => IsInProgress
        ? $"{TimeSpan.FromMilliseconds(WatchedPositionMs):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(WatchedDurationMs):hh\\:mm\\:ss}"
        : string.Empty;
}

public sealed record LibraryExtra(
    long Id,
    int Kind,
    int Ordinal,
    int SortOrder,
    string Title,
    string MediaPath,
    TimeSpan Duration)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(MediaPath) : Title;
    public string DurationText => Duration <= TimeSpan.Zero ? string.Empty : $"{(int)Duration.TotalMinutes} 分钟";
}
