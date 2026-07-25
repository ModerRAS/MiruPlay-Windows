using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

        var cacheDirectory = FileSourceCacheDirectory(root);
        var catalog = LoadDatabase(
            databasePath,
            root,
            path => MlipPath.ResolveLocal(root, path),
            asset => CachedAssetPath(cacheDirectory, asset));
        return CacheFileArtworkPacks(catalog, cacheDirectory, path => MlipPath.ResolveLocal(root, path));
    }

    public static LibraryCatalog LoadRemote(string databasePath, string rootUrl)
    {
        if (!File.Exists(databasePath)) throw new FileNotFoundException("WebDAV MLIP 缓存不存在。", databasePath);
        var normalizedRoot = WebDavMlipClient.NormalizeRoot(rootUrl).AbsoluteUri.TrimEnd('/');
        var artworkDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "artwork");
        return LoadDatabase(
            databasePath,
            normalizedRoot,
            path => MlipPath.ResolveRemote(normalizedRoot, path),
            asset =>
            {
                var path = Path.Combine(artworkDirectory, $"{asset.Sha256}{asset.Extension}");
                return File.Exists(path) ? path : null;
            });
    }

    public static LibraryCatalog LoadSmb(string rootUrl)
    {
        var normalizedRoot = SmbPath.NormalizeRoot(rootUrl);
        var databasePath = Path.Combine(SmbPath.ToUncPath(normalizedRoot), "library.db");
        if (!File.Exists(databasePath)) throw new FileNotFoundException("SMB 目录中没有 library.db。", databasePath);
        var cacheDirectory = FileSourceCacheDirectory(normalizedRoot);
        var catalog = LoadDatabase(
            databasePath,
            normalizedRoot,
            path => SmbPath.ResolveIndexPath(normalizedRoot, path),
            asset => CachedAssetPath(cacheDirectory, asset));
        return CacheFileArtworkPacks(catalog, cacheDirectory, path => SmbPath.ResolveIndexPath(normalizedRoot, path));
    }

    private static LibraryCatalog CacheFileArtworkPacks(
        LibraryCatalog catalog,
        string cacheDirectory,
        Func<string, string> resolvePackPath)
    {
        if (catalog.SchemaVersion < 4) return catalog;
        var neededPackIds = catalog.ArtworkBindings
            .Select(binding => binding.Reference?.Asset.PackId)
            .OfType<long>()
            .ToHashSet();
        var cache = new ArtworkPackCache(cacheDirectory);
        foreach (var pack in catalog.ArtworkPacks.Where(pack => neededPackIds.Contains(pack.Id)).OrderBy(pack => pack.Id))
        {
            if (cache.IsComplete(pack)) continue;
            try
            {
                using var input = new FileStream(resolvePackPath(pack.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                cache.ExtractAsync(pack, input, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"MLIP v4 file artwork pack cache failed: {error.Message}");
            }
        }
        return catalog with
        {
            Series = catalog.Series.Select(series =>
            {
                var asset = series.PosterArtwork?.Asset;
                var cached = asset is null ? null : CachedAssetPath(cacheDirectory, asset);
                return cached is null ? series : series with { PosterPath = cached };
            }).ToList(),
        };
    }

    private static string FileSourceCacheDirectory(string sourceKey)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)))[..24];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "source-cache",
            key);
    }

    private static string? CachedAssetPath(string cacheDirectory, MlipArtworkAsset asset)
    {
        var path = Path.Combine(cacheDirectory, "artwork", $"{asset.Sha256}{asset.Extension}");
        return File.Exists(path) ? path : null;
    }

    private static LibraryCatalog LoadDatabase(
        string databasePath,
        string sourceKey,
        Func<string, string> resolvePath,
        Func<MlipArtworkAsset, string?> resolveAsset)
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
        var artworkPacks = schemaVersion >= 4 ? ReadArtworkPacks(connection) : [];
        var artworkAssets = artworkPacks.SelectMany(pack => pack.Assets).ToDictionary(asset => asset.Id);
        if (schemaVersion >= 4) ValidateArtworkBindings(connection, artworkAssets);
        var artworkBindings = ReadArtworkBindings(connection, schemaVersion, artworkAssets, resolvePath, resolveAsset);
        var posters = artworkBindings
            .Where(binding => binding.OwnerKind == "series" && binding.ArtworkKind == 1)
            .GroupBy(binding => binding.OwnerId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var binding = group.First();
                    return new PosterBinding(
                        binding.Reference is not null ? resolveAsset(binding.Reference.Asset) ?? binding.LegacyPath : binding.LegacyPath,
                        binding.Reference);
                });
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

        return new LibraryCatalog(schemaVersion, sourceKey, series)
        {
            ArtworkPacks = artworkPacks,
            ArtworkBindings = artworkBindings,
        };
    }

    private static int Validate(SqliteConnection connection)
    {
        var schemaVersion = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA user_version") ?? 0, CultureInfo.InvariantCulture);
        if (schemaVersion is < 1 or > 4)
        {
            throw new InvalidDataException($"不支持 MLIP v{schemaVersion}，当前支持 v1-v4。");
        }

        var expected = new HashSet<string>(RequiredTables, StringComparer.OrdinalIgnoreCase);
        if (schemaVersion >= 2)
        {
            expected.Add("series_release_date");
            expected.Add("media_subtitle");
        }
        if (schemaVersion >= 3) expected.Add("media_extra");
        if (schemaVersion >= 4)
        {
            expected.Add("artwork_pack");
            expected.Add("artwork_asset");
        }

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
            throw new InvalidDataException("MLIP v3+ 要求 capability.extra = 1。");
        }
        if (schemaVersion >= 4 && ReadCapability(connection, "artwork_pack") != 1)
        {
            throw new InvalidDataException("MLIP v4 要求 capability.artwork_pack = 1。");
        }

        return schemaVersion;
    }

    private static void ValidateArtworkBindings(
        SqliteConnection connection,
        Dictionary<long, MlipArtworkAsset> assets)
    {
        foreach (var table in new[] { "series_artwork", "episode_artwork" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT path, asset_id FROM {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) && reader.IsDBNull(1))
                    throw new InvalidDataException($"MLIP {table} binding has neither path nor asset_id.");
                if (!reader.IsDBNull(1) && !assets.ContainsKey(reader.GetInt64(1)))
                    throw new InvalidDataException($"MLIP {table} binding references a missing asset.");
            }
        }
    }

    private static List<MlipArtworkBinding> ReadArtworkBindings(
        SqliteConnection connection,
        int schemaVersion,
        Dictionary<long, MlipArtworkAsset> assets,
        Func<string, string> resolvePath,
        Func<MlipArtworkAsset, string?> resolveAsset)
    {
        var result = new List<MlipArtworkBinding>();
        foreach (var (table, ownerKind, ownerColumn) in new[]
        {
            ("series_artwork", "series", "series_id"),
            ("episode_artwork", "episode", "episode_id"),
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = schemaVersion >= 4
                ? $"SELECT {ownerColumn}, artwork_kind, path, asset_id, source_provider, source_subject_id, source_url, downloaded_at FROM {table} ORDER BY id"
                : $"SELECT {ownerColumn}, artwork_kind, path, NULL, NULL, NULL, NULL, NULL FROM {table} ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var legacyPath = reader.IsDBNull(2) ? null : ResolveArtworkPath(reader.GetString(2), resolvePath);
                MlipArtworkReference? reference = null;
                if (!reader.IsDBNull(3))
                {
                    var assetId = reader.GetInt64(3);
                    if (!assets.TryGetValue(assetId, out var asset))
                        throw new InvalidDataException($"MLIP artwork binding references missing asset {assetId}.");
                    reference = new MlipArtworkReference(
                        asset,
                        legacyPath,
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7));
                    _ = resolveAsset(asset);
                }
                if (legacyPath is not null || reference is not null)
                    result.Add(new MlipArtworkBinding(ownerKind, reader.GetInt64(0), reader.GetInt32(1), legacyPath, reference));
            }
        }
        return result;
    }

    private static string? ResolveArtworkPath(string rawPath, Func<string, string> resolvePath)
    {
        var path = rawPath.Trim();
        if (path.Length == 0) return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return path;
        try
        {
            return resolvePath(path);
        }
        catch (InvalidDataException)
        {
            // Invalid artwork does not make otherwise playable media unusable.
            return null;
        }
    }

    private static List<MlipArtworkPack> ReadArtworkPacks(SqliteConnection connection)
    {
        var assetsByPack = new Dictionary<long, List<MlipArtworkAsset>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, sha256, pack_id, member_name, data_offset, byte_length, media_type, width, height
                FROM artwork_asset ORDER BY pack_id, data_offset
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var asset = new MlipArtworkAsset(
                    reader.GetInt64(0),
                    ValidateSha256(reader.GetString(1), "artwork asset"),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8));
                if (asset.DataOffset < 512 || asset.DataOffset % 512 != 0 || asset.DataLength <= 0 || asset.DataLength > 256L * 1024 * 1024)
                    throw new InvalidDataException("MLIP artwork asset has invalid bounds.");
                if (asset.MemberName != $"{asset.Sha256}{asset.Extension}" || asset.Extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
                    throw new InvalidDataException("MLIP artwork asset has an unsafe member name.");
                if (!assetsByPack.TryGetValue(asset.PackId, out var values)) assetsByPack[asset.PackId] = values = [];
                values.Add(asset);
            }
        }

        var packs = new List<MlipArtworkPack>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, path, sha256, byte_length, asset_count FROM artwork_pack ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var path = reader.GetString(1);
                _ = MlipPath.ResolveRemote("https://mlip.invalid/", path);
                var normalizedPath = path.Replace('\\', '/').TrimStart('/');
                if (!normalizedPath.StartsWith("MLIP-Artwork/", StringComparison.Ordinal) ||
                    !normalizedPath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("MLIP artwork pack path must be under MLIP-Artwork/.");
                var byteSize = reader.GetInt64(3);
                var entryCount = reader.GetInt32(4);
                if (byteSize <= 0 || byteSize > 256L * 1024 * 1024 || entryCount <= 0 || entryCount > 4096)
                    throw new InvalidDataException("MLIP artwork pack exceeds safety limits.");
                var assets = assetsByPack.GetValueOrDefault(id, []);
                if (assets.Count != entryCount)
                    throw new InvalidDataException("MLIP artwork pack entry count does not match its catalog.");
                packs.Add(new MlipArtworkPack(id, path, ValidateSha256(reader.GetString(2), "artwork pack"), byteSize, entryCount, assets));
                assetsByPack.Remove(id);
            }
        }
        if (assetsByPack.Count > 0) throw new InvalidDataException("MLIP artwork asset references a missing pack.");
        return packs;
    }

    private static string ValidateSha256(string value, string label)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException($"MLIP {label} SHA-256 is invalid.");
        return normalized;
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
        IReadOnlyDictionary<long, PosterBinding> posters,
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
            var poster = posters.GetValueOrDefault(id);
            result.Add(new LibrarySeries(
                id,
                reader.GetString(1),
                title.Length == 0 ? "Unknown" : title,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                releaseDates.GetValueOrDefault(id),
                genres.GetValueOrDefault(id, []),
                poster?.Path,
                episodes.GetValueOrDefault(id, []),
                extras.GetValueOrDefault(id, []))
            {
                ExternalIds = externalIds.GetValueOrDefault(id, []),
                PosterArtwork = poster?.Reference,
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

    private sealed record PosterBinding(string? Path, MlipArtworkReference? Reference);

    private static bool IsVideo(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is
        "mkv" or "mp4" or "avi" or "mov" or "webm" or "wmv" or "flv" or "m4v";

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
