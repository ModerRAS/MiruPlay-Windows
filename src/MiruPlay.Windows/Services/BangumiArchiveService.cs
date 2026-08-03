using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiruPlay.Windows.Services;

public sealed record BangumiArchiveLatest(
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("content_type")] string? ContentType = null,
    [property: JsonPropertyName("created_at")] string? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt = null,
    string? Digest = null,
    string Name = "",
    long Size = 0);

public sealed record BangumiArchiveSnapshot(
    BangumiArchiveLatest? Latest,
    string SubjectFile,
    long SubjectFileSizeBytes)
{
    public bool HasSubjectData => SubjectFileSizeBytes > 0 && File.Exists(SubjectFile);
}

public sealed record BangumiArchiveSubject(
    int Id,
    string Name,
    string? NameCn = null,
    string? Summary = null,
    IReadOnlyList<string>? Aliases = null,
    string? Date = null,
    int EpisodeCount = 0,
    float? Score = null,
    int? Rank = null);

public sealed record BangumiArchiveSearchHit(
    string AnimeId,
    string Title,
    string MatchedTitle,
    float Confidence,
    string? TitleCn,
    string? Summary,
    string? AirDate,
    int EpisodeCount,
    float? Score,
    int? Rank);

public sealed class BangumiArchiveClient : IDisposable
{
    public const string DefaultLatestUrl = "https://raw.githubusercontent.com/bangumi/Archive/master/aux/latest.json";
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public BangumiArchiveClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _disposeClient = httpClient is null;
    }

    public async Task<BangumiArchiveLatest> FetchLatestAsync(
        string latestUrl = DefaultLatestUrl,
        CancellationToken cancellationToken = default)
    {
        ValidateHttpUrl(latestUrl);
        using var response = await _httpClient.GetAsync(latestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 256 * 1024) throw new InvalidDataException("Bangumi Archive latest.json 过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new LimitedReadStream(stream, 256 * 1024);
        var latest = await JsonSerializer.DeserializeAsync<BangumiArchiveLatest>(limited, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Bangumi Archive latest.json 为空。");
        ValidateHttpUrl(latest.BrowserDownloadUrl);
        if (latest.Size < 0 || latest.Size > BangumiArchiveStore.MaxArchiveBytes)
            throw new InvalidDataException("Bangumi Archive 下载大小无效。");
        return latest;
    }

    public async Task DownloadAsync(
        BangumiArchiveLatest latest,
        string destination,
        long maxBytes = BangumiArchiveStore.MaxArchiveBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateHttpUrl(latest.BrowserDownloadUrl);
        if (latest.Size > maxBytes) throw new InvalidDataException("Bangumi Archive 超过下载大小上限。");
        using var response = await _httpClient.GetAsync(latest.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maxBytes)
            throw new InvalidDataException("Bangumi Archive 超过下载大小上限。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Bangumi Archive 超过下载大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            digest.AppendData(buffer, 0, read);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var expected = latest.Digest?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(expected) && !Convert.ToHexString(digest.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bangumi Archive SHA-256 校验失败。");
    }

    public void Dispose()
    {
        if (_disposeClient) _httpClient.Dispose();
    }

    private static void ValidateHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidDataException("Bangumi Archive URL 无效。");
    }

    private sealed class LimitedReadStream(Stream inner, long maximum) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.CanSeek ? Math.Min(inner.Length, maximum) : _read;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = maximum - _read;
            if (remaining <= 0) throw new InvalidDataException("响应超过大小上限。");
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _read += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = maximum - _read;
            if (remaining <= 0) throw new InvalidDataException("响应超过大小上限。");
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            _read += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class BangumiArchiveStore : IDisposable
{
    public const string SubjectFileName = "subject.jsonlines";
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxSubjectBytes = 2L * 1024 * 1024 * 1024;
    private const int ValidationLineLimit = 50;
    private readonly string _directory;
    private readonly string _subjectFile;
    private readonly string _latestFile;
    private readonly BangumiArchiveClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public BangumiArchiveStore(string directory, BangumiArchiveClient? client = null)
    {
        _directory = Path.GetFullPath(directory.Trim());
        Directory.CreateDirectory(_directory);
        _subjectFile = Path.Combine(_directory, SubjectFileName);
        _latestFile = Path.Combine(_directory, "latest.json");
        _client = client ?? new BangumiArchiveClient();
    }

    public string SubjectFile => _subjectFile;

    public BangumiArchiveSnapshot Snapshot() => new(ReadLatest(), _subjectFile, File.Exists(_subjectFile) ? new FileInfo(_subjectFile).Length : 0);

    public async Task<BangumiArchiveSnapshot> DownloadLatestAsync(
        string latestUrl = BangumiArchiveClient.DefaultLatestUrl,
        CancellationToken cancellationToken = default)
    {
        DeleteTemporaryFiles();
        var latest = await _client.FetchLatestAsync(latestUrl, cancellationToken).ConfigureAwait(false);
        if (File.Exists(_subjectFile) && ReadLatest() == latest) return Snapshot();
        var archive = Path.Combine(_directory, "archive.download");
        var subject = Path.Combine(_directory, $"{SubjectFileName}.download");
        try
        {
            await _client.DownloadAsync(latest, archive, cancellationToken: cancellationToken).ConfigureAwait(false);
            ExtractSubject(archive, subject);
            ValidateSubject(subject);
            Replace(subject, _subjectFile);
            WriteJsonAtomic(_latestFile, latest);
            return Snapshot();
        }
        finally
        {
            DeleteIfExists(archive);
            DeleteIfExists(subject);
        }
    }

    public async Task<BangumiArchiveSnapshot> ImportAsync(
        Stream input,
        string originalName,
        long contentLength,
        long maxBytes = MaxArchiveBytes,
        CancellationToken cancellationToken = default)
    {
        if (contentLength <= 0 || contentLength > maxBytes) throw new InvalidDataException("Bangumi Archive 上传大小无效。");
        DeleteTemporaryFiles();
        var upload = Path.Combine(_directory, "archive.raw-upload");
        var subject = Path.Combine(_directory, $"{SubjectFileName}.upload");
        try
        {
            await CopyBoundedAsync(input, upload, contentLength, maxBytes, cancellationToken).ConfigureAwait(false);
            if (IsZip(upload)) ExtractSubject(upload, subject);
            else File.Copy(upload, subject, true);
            ValidateSubject(subject);
            Replace(subject, _subjectFile);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            WriteJsonAtomic(_latestFile, new BangumiArchiveLatest(
                $"manual://{Path.GetFileName(originalName)}", "application/", now, now, null, originalName, contentLength));
            return Snapshot();
        }
        finally
        {
            DeleteIfExists(upload);
            DeleteIfExists(subject);
        }
    }

    public IReadOnlyList<BangumiArchiveSearchHit> Search(string query, int limit = 10, float minimumConfidence = 0.62f)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0 || !File.Exists(_subjectFile)) return [];
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var subjectId))
        {
            var exact = ReadSubjects().FirstOrDefault(subject => subject.Id == subjectId);
            return exact is null ? [] : [Hit(exact, exact.NameCn ?? exact.Name, 1)];
        }
        var requestedSeason = ExtractSeason(trimmed);
        return ReadSubjects()
            .Select(subject => (Subject: subject, Match: BestMatch(subject, trimmed)))
            .Select(value => (value.Subject, value.Match.Title, Confidence: AdjustSeason(value.Match.Score, requestedSeason, ExtractSeason(value.Match.Title) ?? ExtractSeason(value.Subject.Name))))
            .Where(value => value.Confidence >= minimumConfidence)
            .OrderByDescending(value => value.Confidence)
            .ThenBy(value => value.Subject.Rank ?? int.MaxValue)
            .ThenByDescending(value => value.Subject.Score ?? 0)
            .Take(Math.Max(1, limit))
            .Select(value => Hit(value.Subject, value.Title, value.Confidence))
            .ToList();
    }

    private static BangumiArchiveSearchHit Hit(BangumiArchiveSubject subject, string matchedTitle, float confidence) => new(
        subject.Id.ToString(CultureInfo.InvariantCulture), subject.Name, matchedTitle, confidence,
        subject.NameCn, subject.Summary, subject.Date, subject.EpisodeCount, subject.Score, subject.Rank);

    private static (string Title, float Score) BestMatch(BangumiArchiveSubject subject, string query)
    {
        var normalized = Normalize(query);
        return subject.TitleVariants()
            .Select(title => (Title: title, Score: Score(title, normalized)))
            .OrderByDescending(value => value.Score)
            .First();
    }

    private static float Score(string candidate, string query)
    {
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate == query) return 1;
        if (normalizedCandidate.Contains(query, StringComparison.Ordinal)) return .9f;
        if (query.Contains(normalizedCandidate, StringComparison.Ordinal) && normalizedCandidate.Length >= query.Length * .72) return .9f;
        if (Seasonless(normalizedCandidate) == Seasonless(query) && query.Length > 0) return .88f;
        var candidateCjk = normalizedCandidate.Where(IsCjk).ToHashSet();
        var queryCjk = query.Where(IsCjk).ToHashSet();
        if (candidateCjk.Count > 0 && queryCjk.Count > 0)
        {
            var cjkScore = candidateCjk.Intersect(queryCjk).Count() / (float)Math.Max(candidateCjk.Count, queryCjk.Count);
            if (cjkScore >= .72f) return .82f;
            if (cjkScore >= .56f) return .66f;
        }
        var candidateTokens = Tokens(normalizedCandidate);
        var queryTokens = Tokens(query);
        var overlap = candidateTokens.Intersect(queryTokens).Count();
        if (overlap >= 2 && overlap / (float)Math.Min(candidateTokens.Count, queryTokens.Count) >= .5f) return .7f;
        return .2f;
    }

    private List<BangumiArchiveSubject> ReadSubjects()
    {
        var subjects = new List<BangumiArchiveSubject>();
        foreach (var line in File.ReadLines(_subjectFile))
        {
            if (line.Length == 0) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var subjectId) ||
                    !root.TryGetProperty("type", out var type) || type.GetInt32() != 2 ||
                    !root.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString())) continue;
                subjects.Add(new BangumiArchiveSubject(
                    subjectId,
                    name.GetString()!.Trim(),
                    String(root, "name_cn"),
                    String(root, "summary"),
                    Aliases(root),
                    String(root, "date"),
                    Integer(root, "eps") ?? Integer(root, "total_episodes") ?? 0,
                    Float(root, "score"),
                    Integer(root, "rank")));
            }
            catch (JsonException) { }
        }
        return subjects;
    }

    private static void ValidateSubject(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxSubjectBytes) throw new InvalidDataException("subject.jsonlines 无效。");
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out _) ||
                !root.TryGetProperty("type", out var type) || !type.TryGetInt32(out _))
                throw new InvalidDataException("subject.jsonlines 缺少 Bangumi subject 字段。");
            if (++count >= ValidationLineLimit) break;
        }
        if (count == 0) throw new InvalidDataException("subject.jsonlines 没有可用数据。");
    }

    private static void ExtractSubject(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.FirstOrDefault(value => !string.IsNullOrEmpty(value.Name) && value.Name.Equals(SubjectFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Bangumi Archive 缺少 subject.jsonlines。");
        if (entry.Length > MaxSubjectBytes) throw new InvalidDataException("subject.jsonlines 超过大小上限。");
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output, 64 * 1024);
    }

    private static async Task CopyBoundedAsync(Stream input, string destination, long contentLength, long maxBytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (total < contentLength)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, contentLength - total)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Bangumi Archive 上传数据不完整。");
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Bangumi Archive 上传超过大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsZip(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 4 && header.SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 });
    }

    private BangumiArchiveLatest? ReadLatest()
    {
        if (!File.Exists(_latestFile)) return null;
        try { return JsonSerializer.Deserialize<BangumiArchiveLatest>(File.ReadAllText(_latestFile)); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _client.Dispose();

    private static void Replace(string source, string destination)
    {
        File.Move(source, destination, true);
    }

    private void WriteJsonAtomic(string path, BangumiArchiveLatest latest)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(latest, _jsonOptions));
        File.Move(temp, path, true);
    }

    private void DeleteTemporaryFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.download").Concat(Directory.EnumerateFiles(_directory, "*.upload")).Concat(Directory.EnumerateFiles(_directory, "*.raw-upload")))
            DeleteIfExists(path);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static int? Integer(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static float? Float(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetSingle(out var result) ? result : null;

    private static List<string> Aliases(JsonElement root)
    {
        var aliases = new List<string>();
        if (root.TryGetProperty("meta_tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            aliases.AddRange(tags.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
        if (root.TryGetProperty("infobox", out var infobox) && infobox.ValueKind == JsonValueKind.String)
        {
            foreach (var line in infobox.GetString()!.Split('\n'))
            {
                var key = line.Trim().TrimStart('|').Split('=', 2);
                if (key.Length == 2 && key[0].Trim() is "中文名" or "别名" or "其他名称" or "英文名" or "日文名")
                    aliases.AddRange(key[1].Split(';', '；', '、').Select(value => value.Trim()));
            }
        }
        return aliases.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Normalize(string value) => string.Join(' ', value.Trim().ToLowerInvariant()
        .Replace("：", " ").Replace(':', ' ').Replace('/', ' ').Replace('\\', ' ')
        .Replace('_', ' ').Replace('-', ' ').Replace('(', ' ').Replace(')', ' ')
        .Replace('【', ' ').Replace('】', ' ').Replace('[', ' ').Replace(']', ' ')
        .Replace('.', ' ').Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Seasonless(string value) => System.Text.RegularExpressions.Regex.Replace(value, "(?i)\\b(?:season|s)\\s*\\d+\\b|第\\s*[一二三四五六七八九十\\d]+\\s*[季期]", " ").Trim();

    private static HashSet<string> Tokens(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length > 1 && !int.TryParse(token, out _)).ToHashSet(StringComparer.Ordinal);

    private static bool IsCjk(char value) => value is >= '\u4e00' and <= '\u9fff';

    private static int? ExtractSeason(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, "(?i)\\b(?:season|s)\\s*(?<number>\\d{1,2})\\b|第\\s*(?<number>[一二三四五六七八九十\\d]+)\\s*[季期]");
        return match.Success ? ClassifierText.ExtractNumber(match.Groups["number"].Value) : null;
    }

    private static float AdjustSeason(float score, int? requested, int? candidate) => requested switch
    {
        null => score,
        _ when candidate == requested => Math.Max(score, .94f),
        _ when candidate is not null => Math.Min(score, .48f),
        _ when score >= .9f => .66f,
        _ when score >= .7f => .58f,
        _ => score,
    };
}

internal static class BangumiArchiveSubjectExtensions
{
    public static IEnumerable<string> TitleVariants(this BangumiArchiveSubject subject) =>
        new[] { subject.Name, subject.NameCn }.Concat(subject.Aliases ?? []).Where(value => !string.IsNullOrWhiteSpace(value))!;
}
