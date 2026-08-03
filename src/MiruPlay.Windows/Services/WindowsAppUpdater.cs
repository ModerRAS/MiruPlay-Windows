using System.Reflection;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record WindowsUpdateInfo(
    string VersionName,
    long? VersionCode,
    string ReleaseName,
    string TagName,
    string PublishedAt,
    string ReleaseUrl,
    string AssetName,
    long AssetSizeBytes,
    string DownloadUrl);

public sealed record WindowsUpdateStatus(
    bool Supported,
    string CurrentVersionName,
    long CurrentVersionCode,
    WindowsUpdateInfo? Latest = null,
    bool UpdateAvailable = false,
    long LastCheckedAt = 0,
    string? LastError = null,
    string? StagedInstallerPath = null);

public sealed class WindowsAppUpdater
{
    private const long MaxManifestBytes = 1 * 1024 * 1024;
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri? _manifestUri;
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;
    private readonly string _currentVersionName;
    private readonly long _currentVersionCode;
    private WindowsUpdateStatus _status;

    public WindowsAppUpdater(
        string? manifestUrl = null,
        HttpClient? httpClient = null,
        string? downloadDirectory = null,
        string? currentVersionName = null,
        long? currentVersionCode = null)
    {
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("更新清单地址必须是无凭据、查询或片段的 HTTP(S) 地址。", nameof(manifestUrl));
            _manifestUri = uri;
        }
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "updates");
        _currentVersionName = currentVersionName ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        _currentVersionCode = currentVersionCode ?? 0;
        _status = new WindowsUpdateStatus(_manifestUri is not null, _currentVersionName, _currentVersionCode);
    }

    public bool IsSupported => _manifestUri is not null;
    public WindowsUpdateStatus Status => _status;

    public async Task<WindowsUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_manifestUri is null)
            return _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LastError = "Windows 更新清单尚未配置。" };
        try
        {
            using var response = await _httpClient.GetAsync(_manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await ReadBoundedManifestAsync(stream, cancellationToken).ConfigureAwait(false);
            var info = NormalizeInfo(manifest);
            var available = IsNewer(info);
            return _status = _status with
            {
                Latest = info,
                UpdateAvailable = available,
                LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastError = null,
                StagedInstallerPath = null,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LastError = "更新检查超时。" };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or ArgumentException)
        {
            return _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LastError = RotatingLocalLogStore.Redact(error.Message) };
        }
    }

    public async Task<WindowsUpdateStatus> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_manifestUri is null)
            return _status = _status with { LastError = "Windows 更新清单尚未配置。" };
        var latest = _status.Latest ?? (await CheckAsync(cancellationToken).ConfigureAwait(false)).Latest;
        if (latest is null) return _status = _status with { LastError = "尚未获取可用更新。" };
        if (!IsNewer(latest)) return _status = _status with { LastError = "当前版本已经是最新版本。" };
        var downloadUri = ValidateDownloadUri(latest.DownloadUrl);
        var fileName = Path.GetFileName(latest.AssetName);
        if (fileName.Length is 0 or > 160 || fileName != latest.AssetName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("更新文件名无效。");
        if (latest.AssetSizeBytes is < 1 or > MaxDownloadBytes) throw new InvalidDataException("更新文件大小超出限制。");
        Directory.CreateDirectory(_downloadDirectory);
        var target = Path.Combine(_downloadDirectory, fileName);
        var temporary = $"{target}.{Guid.NewGuid():N}.download";
        try
        {
            using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxDownloadBytes || contentLength is > 0 && contentLength > latest.AssetSizeBytes * 2)
                throw new InvalidDataException("更新响应大小超出限制。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes || total > latest.AssetSizeBytes * 2) throw new InvalidDataException("更新下载大小超出限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (total != latest.AssetSizeBytes) throw new InvalidDataException("更新下载大小与清单不一致。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, target, true);
            return _status = _status with { StagedInstallerPath = target, LastError = null };
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return _status = _status with { LastError = RotatingLocalLogStore.Redact(error.Message) };
        }
    }

    private static async Task<WindowsUpdateInfo> ReadBoundedManifestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxManifestBytes) throw new InvalidDataException("更新清单过大。");
            memory.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<WindowsUpdateInfo>(memory.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("更新清单为空。");
    }

    private static WindowsUpdateInfo NormalizeInfo(WindowsUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.VersionName) || string.IsNullOrWhiteSpace(info.AssetName) || info.AssetSizeBytes <= 0)
            throw new InvalidDataException("更新清单缺少必要字段。");
        _ = ValidateDownloadUri(info.DownloadUrl);
        if (info.AssetSizeBytes > MaxDownloadBytes) throw new InvalidDataException("更新文件大小超出限制。");
        return info with { VersionName = info.VersionName.Trim(), AssetName = info.AssetName.Trim() };
    }

    private bool IsNewer(WindowsUpdateInfo info) => info.VersionCode is long code
        ? code > _currentVersionCode
        : Version.TryParse(info.VersionName.TrimStart('v'), out var latest) &&
          Version.TryParse(_currentVersionName.TrimStart('v'), out var current) && latest > current;

    private static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("更新下载地址必须是 HTTP(S) 地址且不能包含嵌入凭据或片段。");
        return uri;
    }
}
