using Microsoft.Data.Sqlite;

namespace MiruPlay.Windows.Services;

public sealed record RssProcessedItem(long SubscriptionId, string ItemKey, string Title, string Url, long ProcessedAt);

public sealed record RssDownloadTask(
    long Id,
    long SubscriptionId,
    string ItemKey,
    string Title,
    string Url,
    string Status,
    string? Message,
    long CreatedAt,
    long UpdatedAt);

public sealed class RssProcessedStore
{
    private readonly string _connectionString;

    public RssProcessedStore(string? path = null)
    {
        var databasePath = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "rss-state.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS processed_item (
                subscription_id INTEGER NOT NULL,
                item_key TEXT NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                processed_at INTEGER NOT NULL,
                PRIMARY KEY (subscription_id, item_key)
            );
            CREATE TABLE IF NOT EXISTS download_task (
                id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                subscription_id INTEGER NOT NULL,
                item_key TEXT NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                status TEXT NOT NULL,
                message TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                UNIQUE(subscription_id, item_key)
            );
            """;
        command.ExecuteNonQuery();
    }

    public bool IsProcessed(long subscriptionId, string itemKey)
    {
        Validate(subscriptionId, itemKey);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM processed_item WHERE subscription_id = $subscription_id AND item_key = $item_key);";
        command.Parameters.AddWithValue("$subscription_id", subscriptionId);
        command.Parameters.AddWithValue("$item_key", itemKey);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public void MarkProcessed(RssProcessedItem item)
    {
        Validate(item.SubscriptionId, item.ItemKey);
        var title = item.Title.Trim();
        var url = item.Url.Trim();
        if (title.Length > 1_000 || url.Length is 0 or > 4_096) throw new ArgumentException("RSS 已处理条目字段长度无效。", nameof(item));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processed_item (subscription_id, item_key, title, url, processed_at)
            VALUES ($subscription_id, $item_key, $title, $url, $processed_at)
            ON CONFLICT(subscription_id, item_key) DO UPDATE SET
                title = excluded.title,
                url = excluded.url,
                processed_at = excluded.processed_at;
            """;
        command.Parameters.AddWithValue("$subscription_id", item.SubscriptionId);
        command.Parameters.AddWithValue("$item_key", item.ItemKey);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$processed_at", Math.Max(0, item.ProcessedAt));
        command.ExecuteNonQuery();
    }

    public void MarkSubmitted(RssProcessedItem item)
    {
        ValidateItem(item);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var processed = CreateProcessedCommand(connection, transaction, item);
        processed.ExecuteNonQuery();
        using var task = connection.CreateCommand();
        task.Transaction = transaction;
        task.CommandText = """
            INSERT INTO download_task (subscription_id, item_key, title, url, status, message, created_at, updated_at)
            VALUES ($subscription_id, $item_key, $title, $url, 'SUBMITTED', NULL, $created_at, $updated_at)
            ON CONFLICT(subscription_id, item_key) DO UPDATE SET
                title = excluded.title,
                url = excluded.url,
                status = excluded.status,
                message = excluded.message,
                updated_at = excluded.updated_at;
            """;
        task.Parameters.AddWithValue("$subscription_id", item.SubscriptionId);
        task.Parameters.AddWithValue("$item_key", item.ItemKey);
        task.Parameters.AddWithValue("$title", item.Title.Trim());
        task.Parameters.AddWithValue("$url", item.Url.Trim());
        task.Parameters.AddWithValue("$created_at", Math.Max(0, item.ProcessedAt));
        task.Parameters.AddWithValue("$updated_at", Math.Max(0, item.ProcessedAt));
        task.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<RssDownloadTask> ListDownloadTasks(int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, subscription_id, item_key, title, url, status, message, created_at, updated_at
            FROM download_task ORDER BY updated_at DESC, id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var tasks = new List<RssDownloadTask>();
        while (reader.Read())
        {
            tasks.Add(new RssDownloadTask(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8)));
        }
        return tasks;
    }

    public int Count(long subscriptionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM processed_item WHERE subscription_id = $subscription_id;";
        command.Parameters.AddWithValue("$subscription_id", subscriptionId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteCommand CreateProcessedCommand(SqliteConnection connection, SqliteTransaction transaction, RssProcessedItem item)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processed_item (subscription_id, item_key, title, url, processed_at)
            VALUES ($subscription_id, $item_key, $title, $url, $processed_at)
            ON CONFLICT(subscription_id, item_key) DO UPDATE SET
                title = excluded.title,
                url = excluded.url,
                processed_at = excluded.processed_at;
            """;
        command.Parameters.AddWithValue("$subscription_id", item.SubscriptionId);
        command.Parameters.AddWithValue("$item_key", item.ItemKey);
        command.Parameters.AddWithValue("$title", item.Title.Trim());
        command.Parameters.AddWithValue("$url", item.Url.Trim());
        command.Parameters.AddWithValue("$processed_at", Math.Max(0, item.ProcessedAt));
        return command;
    }

    private static void ValidateItem(RssProcessedItem item)
    {
        Validate(item.SubscriptionId, item.ItemKey);
        var title = item.Title.Trim();
        var url = item.Url.Trim();
        if (title.Length > 1_000 || url.Length is 0 or > 4_096) throw new ArgumentException("RSS 已处理条目字段长度无效。", nameof(item));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Validate(long subscriptionId, string itemKey)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);
        if (string.IsNullOrWhiteSpace(itemKey) || itemKey.Length > 4_096)
            throw new ArgumentException("RSS item key 长度无效。", nameof(itemKey));
    }
}
