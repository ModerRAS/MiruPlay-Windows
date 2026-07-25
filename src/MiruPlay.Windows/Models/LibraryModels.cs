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
    public string DisplayNumber => Number % 1 == 0 ? $"第 {Number:0} 集" : $"第 {Number:0.##} 集";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(MediaPath) : Title;
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
    TimeSpan Duration);
