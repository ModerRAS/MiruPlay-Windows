using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record LocalLogRecord(
    string Id,
    long TimestampMs,
    string Level,
    string Message,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? StackTrace = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record LocalLogSnapshot(
    int TotalCount,
    IReadOnlyList<LocalLogRecord> Records)
{
    public int ReturnedCount => Records.Count;
    public int TruncatedCount => Math.Max(0, TotalCount - ReturnedCount);
}

public sealed class RotatingLocalLogStore
{
    private const int MaximumTextCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex SensitiveValue = new(
        @"(?<key>password|token|secret|authorization)(?<separator>\s*[:=]\s*)(?<value>[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UrlUserInfo = new(
        @"(?<scheme>https?://)[^/@\s]+@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string _activePath;
    private readonly string _rotatedPath;
    private readonly long _maxActiveBytes;
    private readonly long _maxRotatedBytes;
    private readonly object _sync = new();

    public RotatingLocalLogStore(
        string? activePath = null,
        long maxActiveBytes = 1 * 1024 * 1024,
        long maxRotatedBytes = 1 * 1024 * 1024)
    {
        if (maxActiveBytes < 1_024 || maxRotatedBytes < 1_024) throw new ArgumentOutOfRangeException(nameof(maxActiveBytes));
        _activePath = activePath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "logs", "miruplay.jsonl")
            : Path.GetFullPath(activePath);
        _rotatedPath = $"{_activePath}.1";
        _maxActiveBytes = maxActiveBytes;
        _maxRotatedBytes = maxRotatedBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(_activePath)!);
        TrimRotatedFile();
    }

    public void Write(
        string level,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var record = new LocalLogRecord(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Truncate(level.Trim().ToUpperInvariant(), 32),
            Truncate(Redact(message), MaximumTextCharacters),
            exception?.GetType().FullName,
            exception is null ? null : Truncate(Redact(exception.Message), MaximumTextCharacters),
            exception is null ? null : Truncate(Redact(exception.ToString()), MaximumTextCharacters),
            attributes?.Take(64).ToDictionary(
                item => Truncate(item.Key, 128),
                item => Truncate(Redact(item.Value), 4_096),
                StringComparer.Ordinal));
        var line = SerializeBounded(record);
        lock (_sync)
        {
            RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(_activePath, line);
        }
    }

    public LocalLogSnapshot ReadRecent(int limit = 200)
    {
        var safeLimit = Math.Clamp(limit, 1, 1_000);
        lock (_sync)
        {
            var records = ReadFiles().ToList();
            return new LocalLogSnapshot(records.Count, records.TakeLast(safeLimit).ToList());
        }
    }

    public IReadOnlyList<LocalLogRecord> ReadPending(int limit = 200)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        lock (_sync) return ReadFiles().Take(safeLimit).ToList();
    }

    public int PendingCount()
    {
        lock (_sync) return ReadFiles().Count();
    }

    public string ExportJsonLines(long? sinceTimestampMs = null, long maxBytes = 4 * 1024 * 1024)
    {
        if (maxBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        lock (_sync)
        {
            using var output = new MemoryStream();
            foreach (var record in ReadFiles().Where(item => sinceTimestampMs is null || item.TimestampMs >= sinceTimestampMs))
            {
                var line = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                if (output.Length + line.Length + 1 > maxBytes) break;
                output.Write(line);
                output.WriteByte((byte)'\n');
            }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
    }

    public void RemoveUploaded(IReadOnlySet<string> ids)
    {
        if (ids.Count == 0) return;
        lock (_sync)
        {
            foreach (var path in new[] { _rotatedPath, _activePath })
            {
                if (!File.Exists(path)) continue;
                var retained = File.ReadLines(path)
                    .Select(line => (Line: line, Record: Deserialize(line)))
                    .Where(item => item.Record is null || !ids.Contains(item.Record.Id))
                    .Select(item => item.Line)
                    .ToList();
                if (retained.Count == 0) File.Delete(path);
                else File.WriteAllLines(path, retained);
            }
        }
    }

    internal static string Redact(string value)
    {
        var redacted = SensitiveValue.Replace(value, match => $"{match.Groups["key"].Value}{match.Groups["separator"].Value}[REDACTED]");
        return UrlUserInfo.Replace(redacted, match => $"{match.Groups["scheme"].Value}[REDACTED]@");
    }

    private string SerializeBounded(LocalLogRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(line) <= _maxActiveBytes) return line;
        var compact = record with
        {
            Message = Truncate(record.Message, (int)Math.Min(MaximumTextCharacters, _maxActiveBytes / 4)),
            ExceptionMessage = null,
            StackTrace = null,
            Attributes = null,
        };
        line = JsonSerializer.Serialize(compact, JsonOptions) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(line) > _maxActiveBytes)
            throw new InvalidDataException("单条日志超过活动日志文件大小限制。");
        return line;
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private IEnumerable<LocalLogRecord> ReadFiles()
    {
        foreach (var path in new[] { _rotatedPath, _activePath })
        {
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                var record = Deserialize(line);
                if (record is not null) yield return record;
            }
        }
    }

    private static LocalLogRecord? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<LocalLogRecord>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        var current = File.Exists(_activePath) ? new FileInfo(_activePath).Length : 0;
        if (current + incomingBytes <= _maxActiveBytes) return;
        if (File.Exists(_rotatedPath)) File.Delete(_rotatedPath);
        if (File.Exists(_activePath)) File.Move(_activePath, _rotatedPath);
        TrimRotatedFile();
    }

    private void TrimRotatedFile()
    {
        if (!File.Exists(_rotatedPath) || new FileInfo(_rotatedPath).Length <= _maxRotatedBytes) return;
        var lines = File.ReadLines(_rotatedPath).ToList();
        while (lines.Count > 0 && new FileInfo(_rotatedPath).Length > _maxRotatedBytes)
        {
            lines.RemoveAt(0);
            File.WriteAllLines(_rotatedPath, lines);
        }
    }
}
