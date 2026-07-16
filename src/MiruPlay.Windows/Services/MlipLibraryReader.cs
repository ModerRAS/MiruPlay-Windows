using System.Globalization;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed class MlipLibraryReader
{
    private static readonly HashSet<string> RequiredTables =
    [
        "meta", "series", "episode", "media_file", "series_artwork", "episode_artwork",
        "genre", "series_genre", "series_external_id", "episode_external_id", "capability",
    ];

    public static LibraryCatalog Load(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var databasePath = Path.Combine(root, "library.db");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("所选目录中没有 library.db。", databasePath);
        }

        return LoadDatabase(databasePath, root, path => MlipPath.ResolveLocal(root, path));
    }

    public static LibraryCatalog LoadRemote(string databasePath, string rootUrl)
    {
        if (!File.Exists(databasePath)) throw new FileNotFoundException("WebDAV MLIP 缓存不存在。", databasePath);
        var normalizedRoot = WebDavMlipClient.NormalizeRoot(rootUrl).AbsoluteUri.TrimEnd('/');
        return LoadDatabase(databasePath, normalizedRoot, path => MlipPath.ResolveRemote(normalizedRoot, path));
    }

    public static LibraryCatalog LoadSmb(string rootUrl)
    {
        var normalizedRoot = SmbPath.NormalizeRoot(rootUrl);
        var databasePath = Path.Combine(SmbPath.ToUncPath(normalizedRoot), "library.db");
        if (!File.Exists(databasePath)) throw new FileNotFoundException("SMB 目录中没有 library.db。", databasePath);
        return LoadDatabase(databasePath, normalizedRoot, path => SmbPath.ResolveIndexPath(normalizedRoot, path));
    }

    private static LibraryCatalog LoadDatabase(
        string databasePath,
        string sourceKey,
        Func<string, string> resolvePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var schemaVersion = Validate(connection);
        var posters = ReadPosters(connection, resolvePath);
        var genres = ReadGenres(connection);
        var releaseDates = TableExists(connection, "series_release_date")
            ? ReadReleaseDates(connection)
            : [];
        var subtitles = TableExists(connection, "media_subtitle")
            ? ReadSubtitles(connection, resolvePath)
            : [];
        var seriesExternalIds = ReadExternalIds(connection, "series_external_id", "series_id");
        var episodeExternalIds = ReadExternalIds(connection, "episode_external_id", "episode_id");
        var episodes = ReadEpisodes(connection, sourceKey, resolvePath, subtitles, episodeExternalIds);
        var extras = schemaVersion >= 3 ? ReadExtras(connection, resolvePath) : [];
        var series = ReadSeries(connection, posters, genres, releaseDates, episodes, extras, seriesExternalIds);

        return new LibraryCatalog(schemaVersion, sourceKey, series);
    }

    private static int Validate(SqliteConnection connection)
    {
        var schemaVersion = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA user_version") ?? 0, CultureInfo.InvariantCulture);
        if (schemaVersion is < 1 or > 3)
        {
            throw new InvalidDataException($"不支持 MLIP v{schemaVersion}，当前支持 v1-v3。");
        }

        var expected = new HashSet<string>(RequiredTables, StringComparer.OrdinalIgnoreCase);
        if (schemaVersion >= 2)
        {
            expected.Add("series_release_date");
            expected.Add("media_subtitle");
        }
        if (schemaVersion >= 3) expected.Add("media_extra");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = command.ExecuteReader();
            while (reader.Read()) expected.Remove(reader.GetString(0));
        }
        if (expected.Count > 0)
        {
            throw new InvalidDataException($"MLIP 缺少数据表：{string.Join(", ", expected.Order())}");
        }

        var protocol = ReadMeta(connection, "protocol");
        var schema = ReadMeta(connection, "schema");
        if (!string.Equals(protocol, "MLIP", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("library.db 不是 MLIP 媒体库。");
        }
        if (schema != schemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException($"meta.schema 与 PRAGMA user_version ({schemaVersion}) 不一致。");
        }
        if (schemaVersion >= 3 && ReadCapability(connection, "extra") != 1)
        {
            throw new InvalidDataException("MLIP v3 要求 capability.extra = 1。");
        }

        return schemaVersion;
    }

    private static Dictionary<long, string> ReadPosters(SqliteConnection connection, Func<string, string> resolvePath)
    {
        var result = new Dictionary<long, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT series_id, path FROM series_artwork WHERE artwork_kind = 1 ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var seriesId = reader.GetInt64(0);
            if (result.ContainsKey(seriesId)) continue;
            var path = reader.GetString(1).Trim();
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                result[seriesId] = path;
                continue;
            }
            try
            {
                result[seriesId] = resolvePath(path);
            }
            catch (InvalidDataException)
            {
                // Invalid artwork does not make otherwise playable media unusable.
            }
        }
        return result;
    }

    private static Dictionary<long, IReadOnlyList<string>> ReadGenres(SqliteConnection connection)
    {
        var result = new Dictionary<long, List<string>>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT series_genre.series_id, genre.name
            FROM series_genre
            INNER JOIN genre ON genre.id = series_genre.genre_id
            ORDER BY genre.name
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var values)) result[id] = values = [];
            values.Add(reader.GetString(1));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    private static Dictionary<long, string> ReadReleaseDates(SqliteConnection connection)
    {
        var result = new Dictionary<long, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT series_id, air_date FROM series_release_date";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(1).Trim();
            if (value.Length > 0) result[reader.GetInt64(0)] = value;
        }
        return result;
    }

    private static Dictionary<long, IReadOnlyList<string>> ReadSubtitles(SqliteConnection connection, Func<string, string> resolvePath)
    {
        var result = new Dictionary<long, List<string>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_file_id, path FROM media_subtitle ORDER BY media_file_id, sort_order, path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var path = resolvePath(reader.GetString(1));
            if (!result.TryGetValue(id, out var values)) result[id] = values = [];
            if (!values.Contains(path, StringComparer.OrdinalIgnoreCase)) values.Add(path);
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    private static Dictionary<long, IReadOnlyList<ExternalMetadataId>> ReadExternalIds(
        SqliteConnection connection,
        string table,
        string parentColumn)
    {
        var result = new Dictionary<long, List<ExternalMetadataId>>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {parentColumn}, provider, value FROM {table} ORDER BY {parentColumn}, provider, value";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var providerValue = reader.GetInt32(1);
            var provider = providerValue switch
            {
                1 => "Bangumi",
                2 => "TMDB",
                3 => "AniDB",
                _ => throw new InvalidDataException($"未知的 MLIP external_id provider：{providerValue}"),
            };
            var value = reader.GetString(2).Trim();
            if (value.Length == 0) throw new InvalidDataException("MLIP external_id 不能为空。");
            var parentId = reader.GetInt64(0);
            if (!result.TryGetValue(parentId, out var values)) result[parentId] = values = [];
            values.Add(new ExternalMetadataId(provider, value));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ExternalMetadataId>)pair.Value);
    }

    private static Dictionary<long, IReadOnlyList<LibraryEpisode>> ReadEpisodes(
        SqliteConnection connection,
        string sourceKey,
        Func<string, string> resolvePath,
        IReadOnlyDictionary<long, IReadOnlyList<string>> subtitles,
        IReadOnlyDictionary<long, IReadOnlyList<ExternalMetadataId>> externalIds)
    {
        var result = new Dictionary<long, List<LibraryEpisode>>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT media_file.id, media_file.path, episode.id, episode.series_id, episode.season,
                   episode.episode, episode.sort_order, episode.title, episode.runtime, episode.uuid
            FROM media_file
            INNER JOIN episode ON episode.id = media_file.episode_id
            ORDER BY episode.series_id, episode.season, episode.sort_order, media_file.path
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var mediaFileId = reader.GetInt64(0);
            var mediaPath = resolvePath(reader.GetString(1));
            var episodeNumber = reader.GetDouble(5);
            if (!IsVideo(mediaPath) || !double.IsFinite(episodeNumber) || episodeNumber != Math.Truncate(episodeNumber)) continue;

            var seriesId = reader.GetInt64(3);
            if (!result.TryGetValue(seriesId, out var values)) result[seriesId] = values = [];
            var episodeUuid = reader.GetString(9);
            values.Add(new LibraryEpisode(
                reader.GetInt64(2),
                episodeUuid,
                PlaybackProgressStore.BuildMlipEpisodeKey(sourceKey, episodeUuid),
                Math.Max(1, reader.GetInt32(4)),
                episodeNumber,
                reader.GetDouble(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                mediaPath,
                TimeSpan.FromSeconds(reader.IsDBNull(8) ? 0 : Math.Max(0, reader.GetInt64(8))),
                subtitles.GetValueOrDefault(mediaFileId, []))
            {
                ExternalIds = externalIds.GetValueOrDefault(reader.GetInt64(2), []),
            });
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LibraryEpisode>)pair.Value);
    }

    private static Dictionary<long, IReadOnlyList<LibraryExtra>> ReadExtras(SqliteConnection connection, Func<string, string> resolvePath)
    {
        var result = new Dictionary<long, List<LibraryExtra>>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, series_id, extra_kind, ordinal, sort_order, title, path, runtime
            FROM media_extra
            ORDER BY series_id, extra_kind, sort_order, path
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var seriesId = reader.GetInt64(1);
            var kind = reader.GetInt32(2);
            if (kind is < 1 or > 5) throw new InvalidDataException($"未知的 MLIP extra_kind：{kind}");
            var mediaPath = resolvePath(reader.GetString(6));
            if (!IsVideo(mediaPath)) throw new InvalidDataException($"MLIP 特典不是视频文件：{reader.GetString(6)}");
            if (!result.TryGetValue(seriesId, out var values)) result[seriesId] = values = [];
            values.Add(new LibraryExtra(
                reader.GetInt64(0),
                kind,
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                mediaPath,
                TimeSpan.FromSeconds(reader.IsDBNull(7) ? 0 : Math.Max(0, reader.GetInt64(7)))));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LibraryExtra>)pair.Value);
    }

    private static List<LibrarySeries> ReadSeries(
        SqliteConnection connection,
        IReadOnlyDictionary<long, string> posters,
        IReadOnlyDictionary<long, IReadOnlyList<string>> genres,
        IReadOnlyDictionary<long, string> releaseDates,
        IReadOnlyDictionary<long, IReadOnlyList<LibraryEpisode>> episodes,
        IReadOnlyDictionary<long, IReadOnlyList<LibraryExtra>> extras,
        IReadOnlyDictionary<long, IReadOnlyList<ExternalMetadataId>> externalIds)
    {
        var result = new List<LibrarySeries>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, uuid, title, original_title, summary, year FROM series ORDER BY title";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var title = reader.GetString(2).Trim();
            result.Add(new LibrarySeries(
                id,
                reader.GetString(1),
                title.Length == 0 ? "Unknown" : title,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                releaseDates.GetValueOrDefault(id),
                genres.GetValueOrDefault(id, []),
                posters.GetValueOrDefault(id),
                episodes.GetValueOrDefault(id, []),
                extras.GetValueOrDefault(id, []))
            {
                ExternalIds = externalIds.GetValueOrDefault(id, []),
            });
        }
        return result;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static int? ReadCapability(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled FROM capability WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool IsVideo(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is
        "mkv" or "mp4" or "avi" or "mov" or "webm" or "wmv" or "flv" or "m4v";

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
