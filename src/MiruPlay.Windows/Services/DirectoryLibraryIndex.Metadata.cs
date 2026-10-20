using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record SeriesMetadataEdit(string SeriesId, NfoTvShowMetadata Metadata);
public sealed record SeriesMetadataChange(string SeriesId, string BeforeTitle, NfoTvShowMetadata Metadata);
public sealed record MetadataBatch(string Id, long SourceId, string State, long CreatedAt, IReadOnlyList<SeriesMetadataChange> Changes);

public sealed partial class DirectoryLibraryIndex
{
    public MetadataBatch PreviewMetadata(long sourceId, string catalogRoot, IReadOnlyList<SeriesMetadataEdit> edits)
    {
        if (edits.Count is < 1 or > 200) throw new ArgumentException("每次元数据预览需要 1 至 200 部作品。", nameof(edits));
        if (edits.Any(item => item is null || item.Metadata is null)) throw new ArgumentException("元数据预览不能包含空条目。", nameof(edits));
        if (edits.Select(item => item.SeriesId).Distinct(StringComparer.Ordinal).Count() != edits.Count)
            throw new ArgumentException("元数据预览包含重复作品。", nameof(edits));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var catalog = LoadDirectory(sourceId, catalogRoot);
        var rows = edits.Select(edit =>
        {
            ValidateMetadata(edit.Metadata);
            var series = catalog.Series.FirstOrDefault(item => item.ApiId == edit.SeriesId)
                ?? throw new KeyNotFoundException("元数据目标作品已不存在，请重新扫描后预览。");
            return new MetadataBatchRow(series.Uuid, new(edit.SeriesId, series.Title, edit.Metadata),
                ReadOverride(connection, transaction, sourceId, series.Uuid), MetadataFingerprint(series));
        }).ToList();
        var id = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO directory_metadata_batch(id, source_id, state, created_at, rows_json) VALUES($id,$source,'PREVIEW',$created,$rows)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$created", createdAt);
        command.Parameters.AddWithValue("$rows", JsonSerializer.Serialize(rows));
        command.ExecuteNonQuery();
        using var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = "DELETE FROM directory_metadata_batch WHERE source_id=$source AND id NOT IN (SELECT id FROM directory_metadata_batch WHERE source_id=$source ORDER BY created_at DESC,rowid DESC LIMIT 50)";
        trim.Parameters.AddWithValue("$source", sourceId);
        trim.ExecuteNonQuery();
        transaction.Commit();
        return new(id, sourceId, "PREVIEW", createdAt, rows.Select(item => item.Change).ToList());
    }

    public MetadataBatch ApplyMetadata(long sourceId, string catalogRoot, string batchId) =>
        ChangeMetadataBatch(sourceId, catalogRoot, batchId, undo: false);

    public MetadataBatch UndoMetadata(long sourceId, string catalogRoot, string batchId) =>
        ChangeMetadataBatch(sourceId, catalogRoot, batchId, undo: true);

    public IReadOnlyList<MetadataBatch> MetadataHistory(long sourceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,state,created_at,rows_json FROM directory_metadata_batch WHERE source_id=$source ORDER BY created_at DESC,rowid DESC LIMIT 50";
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var result = new List<MetadataBatch>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), sourceId, reader.GetString(1), reader.GetInt64(2),
                ReadBatchRows(reader.GetString(3)).Select(item => item.Change).ToList()));
        return result;
    }

    private MetadataBatch ChangeMetadataBatch(long sourceId, string catalogRoot, string id, bool undo)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string state;
        long createdAt;
        List<MetadataBatchRow> rows;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT state,created_at,rows_json FROM directory_metadata_batch WHERE id=$id AND source_id=$source";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$source", sourceId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("元数据预览不存在或已过期。");
            state = reader.GetString(0);
            createdAt = reader.GetInt64(1);
            rows = ReadBatchRows(reader.GetString(2));
        }
        if (state != (undo ? "APPLIED" : "PREVIEW")) throw new InvalidOperationException("元数据批次状态已改变，请刷新后重试。");
        if (!undo && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - createdAt > TimeSpan.FromDays(1).TotalMilliseconds)
            throw new InvalidOperationException("元数据预览已超过 24 小时，请重新预览。");
        var catalog = LoadDirectory(sourceId, catalogRoot);
        foreach (var row in rows)
        {
            var current = ReadOverride(connection, transaction, sourceId, row.SeriesUuid);
            var expected = undo ? JsonSerializer.Serialize(row.Change.Metadata) : row.BeforeOverride;
            if (current != expected) throw new InvalidOperationException("元数据已被其他操作修改，请重新预览，避免覆盖新修改。");
            var series = catalog.Series.FirstOrDefault(item => item.Uuid == row.SeriesUuid)
                ?? throw new InvalidOperationException("作品已从目录移除，请重新扫描。");
            if (!undo && MetadataFingerprint(series) != row.Fingerprint)
                throw new InvalidOperationException("扫描元数据已改变，请重新预览。");
            WriteOverride(connection, transaction, sourceId, row.SeriesUuid,
                undo ? row.BeforeOverride : JsonSerializer.Serialize(row.Change.Metadata));
        }
        state = undo ? "UNDONE" : "APPLIED";
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE directory_metadata_batch SET state=$state WHERE id=$id AND source_id=$source";
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$source", sourceId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return new(id, sourceId, state, createdAt, rows.Select(item => item.Change).ToList());
    }

    private static string? ReadOverride(SqliteConnection connection, SqliteTransaction? transaction, long sourceId, string uuid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT metadata_json FROM directory_series_override WHERE source_id=$source AND series_uuid=$uuid";
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$uuid", uuid);
        return command.ExecuteScalar() as string;
    }

    private static void WriteOverride(SqliteConnection connection, SqliteTransaction transaction, long sourceId, string uuid, string? json)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = json is null
            ? "DELETE FROM directory_series_override WHERE source_id=$source AND series_uuid=$uuid"
            : "INSERT INTO directory_series_override(source_id,series_uuid,metadata_json) VALUES($source,$uuid,$json) ON CONFLICT(source_id,series_uuid) DO UPDATE SET metadata_json=excluded.metadata_json";
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$uuid", uuid);
        if (json is not null) command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
    }

    private static string MetadataFingerprint(LibrarySeries series) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { series.Title, series.OriginalTitle, series.Summary, series.AirDate, series.Genres, series.PosterPath, series.ExternalIds }))));

    private static List<MetadataBatchRow> ReadBatchRows(string json) =>
        ReadMetadata<List<MetadataBatchRow>>(json) ?? throw new InvalidDataException("元数据批次为空。");

    private static void ValidateMetadata(NfoTvShowMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.Title) || metadata.Title.Length > 500 ||
            metadata.OriginalTitle is null or { Length: > 500 } || metadata.Plot is null or { Length: > 20_000 } || metadata.Premiered?.Length > 32 ||
            metadata.Genres?.Count > 100 || metadata.Genres?.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 100) == true ||
            metadata.UniqueIds?.Count > 30 || metadata.UniqueIds?.Any(item => item is null || string.IsNullOrWhiteSpace(item.Type) || item.Type.Length > 50 ||
                string.IsNullOrWhiteSpace(item.Value) || item.Value.Length > 300) == true ||
            metadata.Actors?.Count > 200 || metadata.Actors?.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 300 || item.Role is null or { Length: > 300 }) == true)
            throw new ArgumentException("元数据字段为空或超过长度限制。", nameof(metadata));
    }

    private static LibrarySeries WithMetadata(LibrarySeries series, NfoTvShowMetadata metadata) => series with
    {
        Title = metadata.Title,
        OriginalTitle = metadata.OriginalTitle,
        Summary = metadata.Plot,
        AirDate = metadata.Premiered,
        Year = int.TryParse(metadata.Premiered?.Split('-', '/')[0], out var year) ? year : null,
        Genres = metadata.Genres ?? [],
        ExternalIds = MetadataIds(metadata.UniqueIds),
    };

    private sealed record MetadataBatchRow(string SeriesUuid, SeriesMetadataChange Change, string? BeforeOverride, string Fingerprint);
}
