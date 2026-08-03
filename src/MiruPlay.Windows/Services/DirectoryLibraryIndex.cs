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
    IReadOnlyList<string>? SubtitlePaths = null);

public sealed record DirectoryScanProgress(
    int FilesDiscovered,
    int FilesProcessed,
    int EpisodesFound);

public sealed record DirectoryScanResult(
    int EpisodesFound,
    int NewEpisodes,
    int UpdatedEpisodes,
    int DeletedEpisodes = 0);

public sealed class DirectoryLibraryIndex
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
                   relative_path, subtitle_paths_json
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
            rows.Add(new IndexedEpisode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                relativePath,
                subtitlePaths));
        }

        long episodeId = 0;
        long seriesId = 0;
        var series = rows
            .GroupBy(row => (row.SeriesUuid, row.SeriesTitle))
            .Select(group => new LibrarySeries(
                ++seriesId,
                group.Key.SeriesUuid,
                group.Key.SeriesTitle,
                null,
                "",
                null,
                null,
                [],
                null,
                group.Select(row => new LibraryEpisode(
                    ++episodeId,
                    row.EpisodeUuid,
                    DirectoryEpisodeKey(normalizedRoot, row.RelativePath),
                    row.Season,
                    row.EpisodeNumber,
                    row.Season * 100_000d + row.EpisodeNumber,
                    "",
                    ResolveMediaPath(normalizedRoot, row.RelativePath),
                    TimeSpan.Zero,
                    row.SubtitlePaths.Select(path => ResolveMediaPath(normalizedRoot, path)).ToList())
                {
                    SourceId = sourceId,
                }).ToList(),
                []))
            .ToList();
        return new LibraryCatalog(1, normalizedRoot, series);
    }

    public void Remove(long sourceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM directory_episode WHERE source_id = $sourceId";
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.ExecuteNonQuery();
    }

    internal static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    internal static bool IsSubtitleFile(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));

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
                   relative_path, subtitle_paths_json, size_bytes, modified_ms
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
                reader.GetInt64(7),
                reader.GetInt64(8));
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
                episode_number, relative_path, subtitle_paths_json, size_bytes, modified_ms)
            VALUES(
                $sourceId, $episodeUuid, $seriesUuid, $seriesTitle, $season,
                $episodeNumber, $relativePath, $subtitlePaths, $sizeBytes, $modifiedMs)
            ON CONFLICT(source_id, relative_path) DO UPDATE SET
                episode_uuid = excluded.episode_uuid,
                series_uuid = excluded.series_uuid,
                series_title = excluded.series_title,
                season = excluded.season,
                episode_number = excluded.episode_number,
                subtitle_paths_json = excluded.subtitle_paths_json,
                size_bytes = excluded.size_bytes,
                modified_ms = excluded.modified_ms
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$episodeUuid", row.EpisodeUuid);
        command.Parameters.AddWithValue("$seriesUuid", row.SeriesUuid);
        command.Parameters.AddWithValue("$seriesTitle", row.SeriesTitle);
        command.Parameters.AddWithValue("$season", row.Season);
        command.Parameters.AddWithValue("$episodeNumber", row.EpisodeNumber);
        command.Parameters.AddWithValue("$relativePath", row.RelativePath);
        command.Parameters.AddWithValue("$subtitlePaths", SerializeRelativePaths(row.SubtitlePaths));
        command.Parameters.AddWithValue("$sizeBytes", row.SizeBytes);
        command.Parameters.AddWithValue("$modifiedMs", row.ModifiedMs);
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
        return new IndexedEpisode(
            StableId("episode", relativePath.ToUpperInvariant()),
            "",
            "",
            1,
            0,
            relativePath,
            subtitles,
            entry.SizeBytes,
            entry.ModifiedMs);
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
            SeriesTitle = seriesTitle,
            Season = classification.SeasonNumber,
            EpisodeNumber = episodeNumber,
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
        left.SubtitlePaths.SequenceEqual(right.SubtitlePaths, StringComparer.OrdinalIgnoreCase);

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
            if (!IsVideoFile(path) && !IsSubtitleFile(path)) continue;
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

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, path));
            var info = new FileInfo(path);
            yield return new DirectoryFileEntry(
                relativePath,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                FindSubtitlePaths(subtitlePaths, relativePath));
        }
    }

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
                size_bytes INTEGER NOT NULL DEFAULT 0,
                modified_ms INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(source_id, relative_path)
            );
            CREATE INDEX IF NOT EXISTS directory_episode_series
                ON directory_episode(source_id, series_uuid, season, episode_number);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "subtitle_paths_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "size_bytes", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "modified_ms", "INTEGER NOT NULL DEFAULT 0");
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version = 2";
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
        long SizeBytes = 0,
        long ModifiedMs = 0);
}
