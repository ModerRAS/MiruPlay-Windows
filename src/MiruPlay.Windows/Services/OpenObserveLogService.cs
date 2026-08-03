using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed class OpenObserveTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiruPlay.Windows.OpenObserveToken.v1");
    private readonly string _path;

    public OpenObserveTokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "openobserve-token.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
    }

    public bool IsConfigured => File.Exists(_path);

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var value = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser)).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("OpenObserve 令牌无法解密。", error);
        }
    }

    public void Save(string token)
    {
        var value = token.Trim();
        if (value.Length is 0 or > 4_096) throw new ArgumentException("OpenObserve 令牌长度无效。", nameof(token));
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, _path, true);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public sealed record OpenObserveUploadResult(
    bool Succeeded,
    int UploadedCount,
    string Message,
    long CompletedAt);

public sealed class OpenObserveLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RotatingLocalLogStore _logs;
    private readonly OpenObserveTokenStore _tokens;
    private readonly HttpClient _httpClient;

    public OpenObserveLogService(
        RotatingLocalLogStore logs,
        OpenObserveTokenStore tokens,
        HttpClient? httpClient = null)
    {
        _logs = logs;
        _tokens = tokens;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool TokenConfigured => _tokens.IsConfigured;

    public void SaveToken(string token) => _tokens.Save(token);
    public void ClearToken() => _tokens.Clear();

    public async Task<OpenObserveUploadResult> UploadAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.LogUploadEnabled) return Result(false, 0, "OpenObserve 日志上报未启用。");
        var endpoint = NormalizeEndpoint(settings.LogUploadEndpoint, settings.LogUploadStreamName);
        var token = _tokens.Load();
        if (string.IsNullOrEmpty(token)) return Result(false, 0, "OpenObserve 令牌尚未配置。");
        var records = _logs.ReadPending();
        if (records.Count == 0) return Result(true, 0, "没有待上报日志。");

        var payload = records.Select(record => new Dictionary<string, object?>
        {
            ["_timestamp"] = record.TimestampMs,
            ["level"] = record.Level.ToLowerInvariant(),
            ["tag"] = "MiruPlay.Windows",
            ["log"] = record.Message,
            ["message"] = record.Message,
            ["job"] = "miruplay",
            ["service_name"] = "miruplay-windows",
            ["service_namespace"] = "miruplay",
            ["deployment_environment"] = "windows",
            ["record_id"] = record.Id,
            ["exception_type"] = record.ExceptionType,
            ["exception_message"] = record.ExceptionMessage,
            ["exception_stacktrace"] = record.StackTrace,
        }).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader(token));
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result(false, 0, $"OpenObserve 上报失败：HTTP {(int)response.StatusCode}。");
            _logs.RemoveUploaded(records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal));
            return Result(true, records.Count, $"已上报 {records.Count} 条日志。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(false, 0, "OpenObserve 上报超时。");
        }
        catch (HttpRequestException error)
        {
            return Result(false, 0, $"OpenObserve 上报失败：{RotatingLocalLogStore.Redact(error.Message)}");
        }
    }

    public static string NormalizeEndpoint(string endpoint, string streamName)
    {
        var raw = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("OpenObserve 地址必须是无凭据、查询或片段的 HTTP(S) 地址。", nameof(endpoint));
        var stream = streamName.Trim();
        if (stream.Length is 0 or > 100 || stream.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("OpenObserve stream 名称无效。", nameof(streamName));
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/_json", StringComparison.Ordinal))
            return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
        path = path switch
        {
            "" or "/api" => $"/api/default/{stream}",
            _ when path.EndsWith("/v1/logs", StringComparison.Ordinal) => AppendStream(path[..^"/v1/logs".Length], stream),
            _ when path.EndsWith("/v1/log", StringComparison.Ordinal) => AppendStream(path[..^"/v1/log".Length], stream),
            _ when path.EndsWith("/v1", StringComparison.Ordinal) => AppendStream(path[..^"/v1".Length], stream),
            _ when IsStreamPath(path) => path,
            _ when path.StartsWith("/api/", StringComparison.Ordinal) => $"{path}/{stream}",
            _ => $"{path}/api/default/{stream}",
        };
        path = $"{path.TrimEnd('/')}/_json";
        return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private static string AppendStream(string path, string stream) =>
        string.IsNullOrEmpty(path) ? $"/api/default/{stream}" : IsStreamPath(path) ? path : $"{path.TrimEnd('/')}/{stream}";

    private static bool IsStreamPath(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && segments[0] == "api";
    }

    private static string AuthorizationHeader(string token)
    {
        if (token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return token;
        var value = token.Contains(':', StringComparison.Ordinal)
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(token))
            : token;
        return $"Basic {value}";
    }

    private static OpenObserveUploadResult Result(bool succeeded, int count, string message) =>
        new(succeeded, count, message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
