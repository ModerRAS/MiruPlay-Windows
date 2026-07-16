using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MiruPlay.Windows.Services;

public sealed record PlaybackProgress(
    string EpisodeKey,
    long PositionMs,
    long DurationMs,
    long LastWatchedEpochMs,
    int PlayCount)
{
    public bool IsCompleted => DurationMs > 0 && PositionMs >= DurationMs * 0.9 || DurationMs == 0 && PlayCount > 0;
}

public sealed class PlaybackProgressStore
{
    private readonly string _databasePath;

    public PlaybackProgressStore(string? databasePath = null)
    {
        if (databasePath is null)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiruPlay");
            Directory.CreateDirectory(directory);
            databasePath = Path.Combine(directory, "state.db");
        }
        _databasePath = databasePath;
        Initialize();
    }

    public PlaybackProgress? Get(string episodeKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT episode_key, position_ms, duration_ms, last_watched_ms, play_count
            FROM playback_progress
            WHERE episode_key = $episodeKey
            """;
        command.Parameters.AddWithValue("$episodeKey", episodeKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProgress(reader) : null;
    }

    public IReadOnlyDictionary<string, PlaybackProgress> GetAll()
    {
        var result = new Dictionary<string, PlaybackProgress>(StringComparer.Ordinal);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT episode_key, position_ms, duration_ms, last_watched_ms, play_count
            FROM playback_progress
            ORDER BY last_watched_ms DESC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var progress = ReadProgress(reader);
            result[progress.EpisodeKey] = progress;
        }
        return result;
    }

    public void Save(string episodeKey, long positionMs, long durationMs, bool completed = false)
    {
        var safeDuration = Math.Max(0, durationMs);
        var safePosition = Math.Clamp(positionMs, 0, safeDuration > 0 ? safeDuration : long.MaxValue);
        if (completed && safeDuration > 0) safePosition = safeDuration;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playback_progress(episode_key, position_ms, duration_ms, last_watched_ms, play_count)
            VALUES($episodeKey, $positionMs, $durationMs, $lastWatchedMs, $playCount)
            ON CONFLICT(episode_key) DO UPDATE SET
                position_ms = CASE
                    WHEN $completed = 1 AND excluded.duration_ms = 0 THEN playback_progress.duration_ms
                    ELSE excluded.position_ms
                END,
                duration_ms = CASE WHEN excluded.duration_ms > 0 THEN excluded.duration_ms ELSE playback_progress.duration_ms END,
                last_watched_ms = excluded.last_watched_ms,
                play_count = playback_progress.play_count + excluded.play_count
            """;
        command.Parameters.AddWithValue("$episodeKey", episodeKey);
        command.Parameters.AddWithValue("$positionMs", safePosition);
        command.Parameters.AddWithValue("$durationMs", safeDuration);
        command.Parameters.AddWithValue("$lastWatchedMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$playCount", completed ? 1 : 0);
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public static string BuildMlipEpisodeKey(string libraryRoot, string episodeUuid)
    {
        var normalizedRoot = libraryRoot.StartsWith("\\\\", StringComparison.Ordinal)
            ? SmbPath.NormalizeRoot(libraryRoot).ToUpperInvariant()
            : Uri.TryCreate(libraryRoot, UriKind.Absolute, out var uri)
                ? uri.Scheme switch
                {
                    "http" or "https" => WebDavMlipClient.NormalizeRoot(uri.AbsoluteUri).AbsoluteUri.TrimEnd('/'),
                    "smb" => SmbPath.NormalizeRoot(uri.AbsoluteUri).ToUpperInvariant(),
                    _ => NormalizeLocalRoot(libraryRoot),
                }
                : NormalizeLocalRoot(libraryRoot);
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)))[..16];
        return string.Create(CultureInfo.InvariantCulture, $"mlip:{rootHash}:{episodeUuid}");
    }

    private static string NormalizeLocalRoot(string libraryRoot) =>
        Path.GetFullPath(libraryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS playback_progress(
                episode_key TEXT PRIMARY KEY,
                position_ms INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                last_watched_ms INTEGER NOT NULL,
                play_count INTEGER NOT NULL DEFAULT 0
            );
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
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

    private static PlaybackProgress ReadProgress(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetInt64(3),
        reader.GetInt32(4));
}
