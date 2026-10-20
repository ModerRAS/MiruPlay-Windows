using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record DirectoryFileEntry(
    string RelativePath,
    long SizeBytes,
    long ModifiedMs,
    IReadOnlyList<string>? SubtitlePaths = null,
    NfoEpisodeMetadata? EpisodeMetadata = null,
    NfoTvShowMetadata? SeriesMetadata = null,
    IReadOnlyList<string>? ExternalAudioPaths = null);

public sealed record DirectoryScanProgress(
    int FilesDiscovered,
    int FilesProcessed,
    int EpisodesFound);

public sealed record DirectoryScanResult(
    int EpisodesFound,
    int NewEpisodes,
    int UpdatedEpisodes,
    int DeletedEpisodes = 0);

public sealed partial class DirectoryLibraryIndex
{
    internal const int MaximumEntries = 100_000;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts",
    };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt",
    };
    private readonly string _databasePath;
    private readonly IAnimeVideoClassifier _classifier;

    public DirectoryLibraryIndex(
        string? databasePath = null,
        IAnimeVideoClassifier? classifier = null)
    {
        if (databasePath is null)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiruPlay");
            Directory.CreateDirectory(directory);
            databasePath = Path.Combine(directory, "library-cache.db");
        }
        _databasePath = databasePath;
        _classifier = classifier ?? SharedAnimeVideoClassifier.Instance;
        Initialize();
    }

    public Task<DirectoryScanResult> ScanLocalAsync(
        long sourceId,
        string root,
        CancellationToken cancellationToken = default) =>
        ScanLocalAsync(sourceId, root, progress: null, cancellationToken);

    public Task<DirectoryScanResult> ScanLocalAsync(
        long sourceId,
        string root,
        IProgress<DirectoryScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizeLocalRoot(root);
        return Task.Run(
            () => ScanEntries(sourceId, normalizedRoot, EnumerateLocal(normalizedRoot, cancellationToken).ToList(), progress, cancellationToken),
            cancellationToken);
    }

    public Task<DirectoryScanResult> ScanFileSystemAsync(
        long sourceId,
        string fileSystemRoot,
        string catalogRoot,
        IProgress<DirectoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedFileSystemRoot = NormalizeLocalRoot(fileSystemRoot);
        var normalizedCatalogRoot = NormalizeCatalogRoot(catalogRoot);
        return Task.Run(
            () => ScanEntries(
                sourceId,
                normalizedCatalogRoot,
                EnumerateLocal(normalizedFileSystemRoot, cancellationToken).ToList(),
                progress,
                cancellationToken),
            cancellationToken);
    }

    public Task<DirectoryScanResult> ScanAsync(
        long sourceId,
        string catalogRoot,
        IReadOnlyList<DirectoryFileEntry> entries,
        IProgress<DirectoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ScanEntries(sourceId, NormalizeCatalogRoot(catalogRoot), entries, progress, cancellationToken),
            cancellationToken);

    public LibraryCatalog LoadLocal(long sourceId, string root) => LoadDirectory(sourceId, NormalizeLocalRoot(root));

    public LibraryCatalog LoadDirectory(long sourceId, string catalogRoot)
    {
        var normalizedRoot = NormalizeCatalogRoot(catalogRoot);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT episode_uuid, series_uuid, series_title, season, episode_number,
                   relative_path, subtitle_paths_json, external_audio_paths_json, episode_metadata_json, series_metadata_json
            FROM directory_episode
            WHERE source_id = $sourceId
            ORDER BY series_title COLLATE NOCASE, season, episode_number, relative_path COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        using var reader = command.ExecuteReader();
        var rows = new List<IndexedEpisode>();
        while (reader.Read())
        {
            var relativePath = NormalizeRelativePath(reader.GetString(5));
            var subtitlePaths = DeserializeRelativePaths(reader.GetString(6));
            var externalAudioPaths = DeserializeRelativePaths(reader.GetString(7));
            rows.Add(new IndexedEpisode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                relativePath,
                subtitlePaths,
                ExternalAudioPaths: externalAudioPaths,
                EpisodeMetadata: ReadMetadata<NfoEpisodeMetadata>(reader.GetString(8)),
                SeriesMetadata: ReadMetadata<NfoTvShowMetadata>(reader.GetString(9))));
        }

        long episodeId = 0;
        long seriesId = 0;
        var series = rows
            .GroupBy(row => (row.SeriesUuid, row.SeriesTitle))
            .Select(group =>
            {
                var metadata = group.Select(row => row.SeriesMetadata).FirstOrDefault(item => item is not null);
                return new LibrarySeries(
                ++seriesId,
                group.Key.SeriesUuid,
                group.Key.SeriesTitle,
                metadata?.OriginalTitle,
                metadata?.Plot ?? "",
                int.TryParse(metadata?.Premiered?.Split('-', '/')[0], out var year) ? year : null,
                metadata?.Premiered,
                metadata?.Genres ?? [],
                null,
                group.Select(row => new LibraryEpisode(
                    ++episodeId,
                    row.EpisodeUuid,
                    DirectoryEpisodeKey(normalizedRoot, row.RelativePath),
                    row.Season,
                    row.EpisodeNumber,
                    row.Season * 100_000d + row.EpisodeNumber,
                    row.EpisodeMetadata?.Title ?? "",
                    ResolveMediaPath(normalizedRoot, row.RelativePath),
                    TimeSpan.Zero,
                    row.SubtitlePaths.Select(path => ResolveMediaPath(normalizedRoot, path)).ToList())
                {
                    SourceId = sourceId,
                    ExternalAudioPaths = row.ExternalAudioPaths.Select(path => ResolveMediaPath(normalizedRoot, path)).ToList(),
                    ExternalIds = MetadataIds(row.EpisodeMetadata?.UniqueIds),
                }).ToList(),
                [])
                {
                    ExternalIds = MetadataIds(metadata?.UniqueIds),
                };
            })
            .ToList();
        series = series.Select(item => ReadOverride(connection, null, sourceId, item.Uuid) is string json
            ? WithMetadata(item, ReadMetadata<NfoTvShowMetadata>(json) ?? throw new InvalidDataException("本地元数据覆盖为空。"))
            : item).ToList();
        return new LibraryCatalog(1, normalizedRoot, series);
    }

    public void Remove(long sourceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM directory_episode WHERE source_id = $sourceId;
            DELETE FROM directory_series_override WHERE source_id = $sourceId;
            DELETE FROM directory_metadata_batch WHERE source_id = $sourceId;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.ExecuteNonQuery();
    }

    internal static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    internal static bool IsSubtitleFile(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));
    internal static bool IsExternalAudioFile(string path) => ExternalAudioPathMatcher.IsSupported(path);

    private DirectoryScanResult ScanEntries(
        long sourceId,
        string catalogRoot,
        IReadOnlyList<DirectoryFileEntry> entries,
        IProgress<DirectoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (entries.Count > MaximumEntries)
            throw new InvalidDataException($"目录扫描条目数超过 {MaximumEntries} 限制。");
        var scanned = new Dictionary<string, IndexedEpisode>(StringComparer.OrdinalIgnoreCase);
        var fallbackNumber = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeEntry(entry, catalogRoot);
            if (!IsVideoFile(normalized.RelativePath)) continue;
            if (!scanned.TryAdd(normalized.RelativePath, Classify(normalized, ref fallbackNumber)))
                throw new InvalidDataException("目录扫描发现重复的媒体路径。");
            progress?.Report(new DirectoryScanProgress(scanned.Count, scanned.Count, scanned.Count));
        }

        var existing = LoadRows(sourceId);
        var newEpisodes = scanned.Keys.Count(path => !existing.ContainsKey(path));
        var updatedEpisodes = scanned.Count(pair =>
            existing.TryGetValue(pair.Key, out var oldRow) && !Equivalent(oldRow, pair.Value));
        var deletedEpisodes = existing.Keys.Count(path => !scanned.ContainsKey(path));

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var path in existing.Keys.Where(path => !scanned.ContainsKey(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM directory_episode WHERE source_id = $sourceId AND relative_path = $relativePath";
            delete.Parameters.AddWithValue("$sourceId", sourceId);
            delete.Parameters.AddWithValue("$relativePath", path);
            delete.ExecuteNonQuery();
        }

        var processed = 0;
        foreach (var row in scanned.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!existing.TryGetValue(row.RelativePath, out var oldRow) || !Equivalent(oldRow, row))
                Upsert(connection, transaction, sourceId, row);
            progress?.Report(new DirectoryScanProgress(scanned.Count, ++processed, scanned.Count));
        }
        transaction.Commit();
        return new DirectoryScanResult(scanned.Count, newEpisodes, updatedEpisodes, deletedEpisodes);
    }

    private Dictionary<string, IndexedEpisode> LoadRows(long sourceId)
    {
        var result = new Dictionary<string, IndexedEpisode>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT episode_uuid, series_uuid, series_title, season, episode_number,
                   relative_path, subtitle_paths_json, external_audio_paths_json, size_bytes, modified_ms, episode_metadata_json, series_metadata_json
            FROM directory_episode
            WHERE source_id = $sourceId
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = NormalizeRelativePath(reader.GetString(5));
            result[relativePath] = new IndexedEpisode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                relativePath,
                DeserializeRelativePaths(reader.GetString(6)),
                DeserializeRelativePaths(reader.GetString(7)),
                reader.GetInt64(8),
                reader.GetInt64(9),
                ReadMetadata<NfoEpisodeMetadata>(reader.GetString(10)),
                ReadMetadata<NfoTvShowMetadata>(reader.GetString(11)));
        }
        return result;
    }

    private static void Upsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceId,
        IndexedEpisode row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO directory_episode(
                source_id, episode_uuid, series_uuid, series_title, season,
                episode_number, relative_path, subtitle_paths_json, external_audio_paths_json, size_bytes, modified_ms,
                episode_metadata_json, series_metadata_json)
            VALUES(
                $sourceId, $episodeUuid, $seriesUuid, $seriesTitle, $season,
                $episodeNumber, $relativePath, $subtitlePaths, $externalAudioPaths, $sizeBytes, $modifiedMs, $episodeMetadata, $seriesMetadata)
            ON CONFLICT(source_id, relative_path) DO UPDATE SET
                episode_uuid = excluded.episode_uuid,
                series_uuid = excluded.series_uuid,
                series_title = excluded.series_title,
                season = excluded.season,
                episode_number = excluded.episode_number,
                subtitle_paths_json = excluded.subtitle_paths_json,
                external_audio_paths_json = excluded.external_audio_paths_json,
                size_bytes = excluded.size_bytes,
                modified_ms = excluded.modified_ms,
                episode_metadata_json = excluded.episode_metadata_json,
                series_metadata_json = excluded.series_metadata_json
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$episodeUuid", row.EpisodeUuid);
        command.Parameters.AddWithValue("$seriesUuid", row.SeriesUuid);
        command.Parameters.AddWithValue("$seriesTitle", row.SeriesTitle);
        command.Parameters.AddWithValue("$season", row.Season);
        command.Parameters.AddWithValue("$episodeNumber", row.EpisodeNumber);
        command.Parameters.AddWithValue("$relativePath", row.RelativePath);
        command.Parameters.AddWithValue("$subtitlePaths", SerializeRelativePaths(row.SubtitlePaths));
        command.Parameters.AddWithValue("$externalAudioPaths", SerializeRelativePaths(row.ExternalAudioPaths));
        command.Parameters.AddWithValue("$sizeBytes", row.SizeBytes);
        command.Parameters.AddWithValue("$modifiedMs", row.ModifiedMs);
        command.Parameters.AddWithValue("$episodeMetadata", JsonSerializer.Serialize(row.EpisodeMetadata));
        command.Parameters.AddWithValue("$seriesMetadata", JsonSerializer.Serialize(row.SeriesMetadata));
        command.ExecuteNonQuery();
    }

    private static IndexedEpisode NormalizeEntry(DirectoryFileEntry entry, string catalogRoot)
    {
        var relativePath = NormalizeRelativePath(entry.RelativePath);
        _ = ResolveMediaPath(catalogRoot, relativePath);
        var subtitles = (entry.SubtitlePaths ?? [])
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var subtitle in subtitles) _ = ResolveMediaPath(catalogRoot, subtitle);
        var externalAudio = (entry.ExternalAudioPaths ?? [])
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var audio in externalAudio) _ = ResolveMediaPath(catalogRoot, audio);
        return new IndexedEpisode(
            StableId("episode", relativePath.ToUpperInvariant()),
            "",
            "",
            1,
            0,
            relativePath,
            subtitles,
            externalAudio,
            entry.SizeBytes,
            entry.ModifiedMs,
            entry.EpisodeMetadata,
            entry.SeriesMetadata);
    }

    private IndexedEpisode Classify(IndexedEpisode entry, ref int fallbackNumber)
    {
        var localPath = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var parentName = Path.GetFileName(Path.GetDirectoryName(localPath));
        var classification = _classifier.Classify(
            entry.RelativePath,
            Path.GetFileName(localPath),
            parentName);
        var seriesTitle = classification.ShowName == "Unknown" && !string.IsNullOrWhiteSpace(parentName)
            ? parentName
            : classification.ShowName;
        var episodeNumber = classification.EpisodeNumber ?? ++fallbackNumber;
        return entry with
        {
            SeriesUuid = StableId("series", seriesTitle.ToUpperInvariant()),
            SeriesTitle = !string.IsNullOrWhiteSpace(entry.SeriesMetadata?.Title) ? entry.SeriesMetadata.Title
                : !string.IsNullOrWhiteSpace(entry.EpisodeMetadata?.ShowTitle) ? entry.EpisodeMetadata.ShowTitle : seriesTitle,
            Season = entry.EpisodeMetadata?.Season is >= 0 ? entry.EpisodeMetadata.Season : classification.SeasonNumber,
            EpisodeNumber = entry.EpisodeMetadata?.Episode is >= 0 ? entry.EpisodeMetadata.Episode : episodeNumber,
        };
    }

    private static bool Equivalent(IndexedEpisode left, IndexedEpisode right) =>
        left.EpisodeUuid == right.EpisodeUuid &&
        left.SeriesUuid == right.SeriesUuid &&
        left.SeriesTitle == right.SeriesTitle &&
        left.Season == right.Season &&
        left.EpisodeNumber == right.EpisodeNumber &&
        left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase) &&
        left.SizeBytes == right.SizeBytes &&
        left.ModifiedMs == right.ModifiedMs &&
        JsonSerializer.Serialize(left.EpisodeMetadata) == JsonSerializer.Serialize(right.EpisodeMetadata) &&
        JsonSerializer.Serialize(left.SeriesMetadata) == JsonSerializer.Serialize(right.SeriesMetadata) &&
        left.SubtitlePaths.SequenceEqual(right.SubtitlePaths, StringComparer.OrdinalIgnoreCase) &&
        left.ExternalAudioPaths.SequenceEqual(right.ExternalAudioPaths, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<DirectoryFileEntry> EnumerateLocal(string root, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var relevantPaths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVideoFile(path) && !IsSubtitleFile(path) && !IsExternalAudioFile(path)) continue;
            if (relevantPaths.Count >= MaximumEntries)
                throw new InvalidDataException($"目录扫描条目数超过 {MaximumEntries} 限制。");
            relevantPaths.Add(path);
        }
        var files = relevantPaths
            .Where(IsVideoFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var subtitlePaths = relevantPaths
            .Where(IsSubtitleFile)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var externalAudioPaths = relevantPaths
            .Where(IsExternalAudioFile)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .ToList();

        var nfo = new NfoDocumentService(root);
        var seriesMetadata = new Dictionary<string, NfoTvShowMetadata?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, path));
            var info = new FileInfo(path);
            var nfoPath = nfo.EpisodePath(path);
            var episodeMetadata = File.Exists(nfoPath) ? nfo.ReadEpisode(nfoPath) : null;
            var directory = Path.GetDirectoryName(path)!;
            var metadata = FindSeriesMetadata(nfo, root, directory, seriesMetadata);
            yield return new DirectoryFileEntry(
                relativePath,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                FindSubtitlePaths(subtitlePaths, relativePath),
                episodeMetadata,
                metadata,
                ExternalAudioPathMatcher.Find(relativePath, externalAudioPaths));
        }
    }

    private static NfoTvShowMetadata? FindSeriesMetadata(
        NfoDocumentService nfo,
        string root,
        string directory,
        Dictionary<string, NfoTvShowMetadata?> cache)
    {
        if (cache.TryGetValue(directory, out var metadata)) return metadata;
        var path = nfo.TvShowPath(directory);
        metadata = File.Exists(path) ? nfo.ReadTvShow(path)
            : directory.Equals(root, StringComparison.OrdinalIgnoreCase) ? null
            : FindSeriesMetadata(nfo, root, Path.GetDirectoryName(directory)!, cache);
        cache[directory] = metadata;
        return metadata;
    }

    private static T? ReadMetadata<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException error) { throw new InvalidDataException("目录索引 NFO 元数据格式无效。", error); }
    }

    private static List<ExternalMetadataId> MetadataIds(IReadOnlyList<NfoUniqueId>? ids) =>
        (ids ?? []).Where(id => id.Value.Length > 0).Select(id => new ExternalMetadataId(
            id.Type.ToLowerInvariant() switch
            {
                "bangumi" => "Bangumi",
                "tmdb" => "TMDB",
                "tvmaze" => "TVMaze",
                "anidb" => "AniDB",
                _ => id.Type,
            }, id.Value)).ToList();

    private static List<string> FindSubtitlePaths(IEnumerable<string> subtitles, string mediaPath)
    {
        var cleanMedia = NormalizeRelativePath(mediaPath);
        var slash = cleanMedia.LastIndexOf('/');
        var directory = slash >= 0 ? cleanMedia[..slash] : "";
        var mediaName = slash >= 0 ? cleanMedia[(slash + 1)..] : cleanMedia;
        var extensionStart = mediaName.LastIndexOf('.');
        var stem = extensionStart > 0 ? mediaName[..extensionStart] : mediaName;
        var prefix = $"{stem}.";
        return subtitles
            .Where(path =>
            {
                var clean = NormalizeRelativePath(path);
                var pathSlash = clean.LastIndexOf('/');
                var pathDirectory = pathSlash >= 0 ? clean[..pathSlash] : "";
                var fileName = pathSlash >= 0 ? clean[(pathSlash + 1)..] : clean;
                return pathDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string NormalizeLocalRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullPath = Path.GetFullPath(root.Trim());
        if (Directory.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("媒体目录根不能是重解析点。");
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"媒体目录不存在：{fullPath}");
        _ = Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToList();
        return TrimDirectorySeparator(fullPath);
    }

    private static string NormalizeCatalogRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (Uri.TryCreate(root.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return WebDavMlipClient.NormalizeRoot(uri.AbsoluteUri).AbsoluteUri.TrimEnd('/');
        if (root.Trim().StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            return SmbPath.NormalizeRoot(root);
        return TrimDirectorySeparator(Path.GetFullPath(root.Trim()));
    }

    private static string ResolveMediaPath(string root, string relativePath)
    {
        var relative = NormalizeRelativePath(relativePath);
        if (root.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            return SmbPath.ResolveIndexPath(root, relative);
        if (Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var safeSegments = SafeRelativeSegments(relative);
            return new Uri(WebDavMlipClient.NormalizeRoot(root), string.Join('/', safeSegments.Select(Uri.EscapeDataString))).AbsoluteUri;
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var relativeCheck = Path.GetRelativePath(root, fullPath);
        if (relativeCheck == ".." || relativeCheck.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("媒体索引路径超出来源根目录。");
        return fullPath;
    }

    private static string[] SafeRelativeSegments(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOfAny(['/', '\\', '\0', ':']) >= 0))
            throw new InvalidDataException("目录索引路径包含不安全的目录段。");
        return segments;
    }

    private static string NormalizeRelativePath(string path)
    {
        var clean = path.Trim().Replace('\\', '/');
        if (clean.StartsWith('/'))
            throw new InvalidDataException("目录索引路径不能是绝对路径。");
        _ = SafeRelativeSegments(clean);
        return string.Join('/', clean.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TrimDirectorySeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && path.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string SerializeRelativePaths(IReadOnlyList<string> paths) =>
        JsonSerializer.Serialize(paths);

    private static List<string> DeserializeRelativePaths(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("目录索引字幕路径格式无效。", error);
        }
    }

    private static string StableId(string kind, string value) =>
        $"directory-{kind}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";

    private static string DirectoryEpisodeKey(string root, string relativePath)
    {
        var source = $"{root.ToUpperInvariant()}\n{relativePath.ToUpperInvariant()}";
        return $"directory:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32].ToLowerInvariant()}";
    }

    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS directory_episode(
                source_id INTEGER NOT NULL,
                episode_uuid TEXT NOT NULL,
                series_uuid TEXT NOT NULL,
                series_title TEXT NOT NULL,
                season INTEGER NOT NULL,
                episode_number REAL NOT NULL,
                relative_path TEXT NOT NULL,
                subtitle_paths_json TEXT NOT NULL DEFAULT '[]',
                external_audio_paths_json TEXT NOT NULL DEFAULT '[]',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                modified_ms INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(source_id, relative_path)
            );
            CREATE INDEX IF NOT EXISTS directory_episode_series
                ON directory_episode(source_id, series_uuid, season, episode_number);
            CREATE TABLE IF NOT EXISTS directory_series_override(
                source_id INTEGER NOT NULL,
                series_uuid TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                PRIMARY KEY(source_id,series_uuid)
            );
            CREATE TABLE IF NOT EXISTS directory_metadata_batch(
                id TEXT PRIMARY KEY,
                source_id INTEGER NOT NULL,
                state TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                rows_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "subtitle_paths_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "external_audio_paths_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "size_bytes", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "modified_ms", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "episode_metadata_json", "TEXT NOT NULL DEFAULT 'null'");
        EnsureColumn(connection, "series_metadata_json", "TEXT NOT NULL DEFAULT 'null'");
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version = 3";
        version.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        var exists = false;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(directory_episode)";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE directory_episode ADD COLUMN {name} {definition}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record IndexedEpisode(
        string EpisodeUuid,
        string SeriesUuid,
        string SeriesTitle,
        int Season,
        double EpisodeNumber,
        string RelativePath,
        IReadOnlyList<string> SubtitlePaths,
        IReadOnlyList<string> ExternalAudioPaths,
        long SizeBytes = 0,
        long ModifiedMs = 0,
        NfoEpisodeMetadata? EpisodeMetadata = null,
        NfoTvShowMetadata? SeriesMetadata = null);
}
