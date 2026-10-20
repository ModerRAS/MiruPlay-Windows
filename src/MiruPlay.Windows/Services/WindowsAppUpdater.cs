using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    string DownloadUrl,
    string Sha256 = "");

public sealed record WindowsUpdateStatus(
    bool Supported,
    string CurrentVersionName,
    long CurrentVersionCode,
    WindowsUpdateInfo? Latest = null,
    bool UpdateAvailable = false,
    long LastCheckedAt = 0,
    string? LastError = null,
    string? StagedInstallerPath = null,
    string ManifestUrl = "");

public sealed class WindowsAppUpdater : IDisposable
{
    private const long MaxManifestBytes = 1 * 1024 * 1024;
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Uri? _manifestUri;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
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
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttpClient = httpClient is null;
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "updates");
        _currentVersionName = currentVersionName ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        _currentVersionCode = currentVersionCode ?? 0;
        _status = new WindowsUpdateStatus(false, _currentVersionName, _currentVersionCode);
        ConfigureManifest(manifestUrl);
    }

    public void ConfigureManifest(string? manifestUrl)
    {
        var normalized = NormalizeManifestUrl(manifestUrl);
        _manifestUri = normalized.Length == 0 ? null : new Uri(normalized, UriKind.Absolute);
        _status = new WindowsUpdateStatus(
            _manifestUri is not null, _currentVersionName, _currentVersionCode, ManifestUrl: _manifestUri?.AbsoluteUri ?? string.Empty);
    }

    public static string NormalizeManifestUrl(string? manifestUrl) => string.IsNullOrWhiteSpace(manifestUrl)
        ? string.Empty
        : ValidateManifestUri(manifestUrl).AbsoluteUri;

    public bool IsSupported => _manifestUri is not null;
    public WindowsUpdateStatus Status => _status;

    public async Task<WindowsUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_manifestUri is null)
            return _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LastError = AppStrings.Get("upd.noManifest") };
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
            return _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LastError = AppStrings.Get("upd.checkTimeout") };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or ArgumentException)
        {
            return _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LastError = RotatingLocalLogStore.Redact(error.Message) };
        }
    }

    public async Task<WindowsUpdateStatus> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_manifestUri is null)
            return _status = _status with { LastError = AppStrings.Get("upd.noManifest") };
        var latest = _status.Latest ?? (await CheckAsync(cancellationToken).ConfigureAwait(false)).Latest;
        if (latest is null) return _status = _status with { LastError = AppStrings.Get("upd.noUpdate") };
        if (!IsNewer(latest)) return _status = _status with { LastError = AppStrings.Get("upd.latest") };
        var downloadUri = ValidateDownloadUri(latest.DownloadUrl);
        var fileName = Path.GetFileName(latest.AssetName);
        if (fileName.Length is 0 or > 160 || fileName != latest.AssetName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException(AppStrings.Get("upd.badFilename"));
        if (latest.AssetSizeBytes is < 1 or > MaxDownloadBytes) throw new InvalidDataException(AppStrings.Get("upd.sizeLimit"));
        Directory.CreateDirectory(_downloadDirectory);
        var target = Path.Combine(_downloadDirectory, fileName);
        var temporary = $"{target}.{Guid.NewGuid():N}.download";
        try
        {
            using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxDownloadBytes || contentLength is > 0 && contentLength > latest.AssetSizeBytes * 2)
                throw new InvalidDataException(AppStrings.Get("upd.responseLimit"));
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes || total > latest.AssetSizeBytes * 2) throw new InvalidDataException(AppStrings.Get("upd.downloadLimit"));
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (total != latest.AssetSizeBytes) throw new InvalidDataException(AppStrings.Get("upd.sizeMismatch"));
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(latest.Sha256)))
                    throw new InvalidDataException(AppStrings.Get("upd.hashMismatch"));
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

    public int StartInstallerHandoff(string currentExecutable, int parentProcessId, bool backgroundMode = false)
    {
        var script = BuildInstallerHandoffScript(currentExecutable, parentProcessId, backgroundMode);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(AppStrings.Get("upd.noHandoff"));
        return process.Id;
    }

    internal string BuildInstallerHandoffScript(string currentExecutable, int parentProcessId, bool backgroundMode = false)
    {
        var staged = _status.StagedInstallerPath ?? throw new InvalidOperationException(AppStrings.Get("upd.noStaged"));
        var fullPath = Path.GetFullPath(staged);
        var basePath = Path.GetFullPath(_downloadDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) || parentProcessId < 1)
            throw new InvalidOperationException(AppStrings.Get("upd.badStaged"));
        var expected = _status.Latest?.Sha256 ?? throw new InvalidOperationException(AppStrings.Get("upd.noHash"));
        using (var input = File.OpenRead(fullPath))
        {
            var actual = Convert.ToHexString(SHA256.HashData(input));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
                throw new InvalidDataException(AppStrings.Get("upd.hashFailed"));
        }
        var executable = Path.GetFullPath(currentExecutable);
        var restart = backgroundMode ? " -ArgumentList '--background'" : string.Empty;
        var logPath = Path.Combine(_downloadDirectory, "installer-handoff.log");
        return $"$ErrorActionPreference='Stop'; $log={PowerShellLiteral(logPath)}; try {{ Wait-Process -Id {parentProcessId} -ErrorAction SilentlyContinue; $p=Start-Process -FilePath {PowerShellLiteral(fullPath)} -ArgumentList '/VERYSILENT','/NORESTART' -Wait -PassThru; if ($p.ExitCode -ne 0) {{ throw ('Installer exited with code ' + $p.ExitCode) }}; Start-Process -FilePath {PowerShellLiteral(executable)}{restart} }} catch {{ [IO.File]::WriteAllText($log, $_.Exception.Message) }}";
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<WindowsUpdateInfo> ReadBoundedManifestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxManifestBytes) throw new InvalidDataException(AppStrings.Get("upd.tooLarge"));
            memory.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<WindowsUpdateInfo>(memory.ToArray(), JsonOptions)
            ?? throw new InvalidDataException(AppStrings.Get("upd.empty"));
    }

    private static WindowsUpdateInfo NormalizeInfo(WindowsUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.VersionName) || string.IsNullOrWhiteSpace(info.AssetName) ||
            string.IsNullOrWhiteSpace(info.DownloadUrl) || string.IsNullOrWhiteSpace(info.Sha256) || info.AssetSizeBytes <= 0 ||
            info.Sha256.Length != 64 || info.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(AppStrings.Get("upd.badManifest"));
        _ = ValidateDownloadUri(info.DownloadUrl);
        if (info.AssetSizeBytes > MaxDownloadBytes) throw new InvalidDataException(AppStrings.Get("upd.sizeLimit"));
        return info with { VersionName = info.VersionName.Trim(), AssetName = info.AssetName.Trim() };
    }

    private bool IsNewer(WindowsUpdateInfo info) => info.VersionCode is long code
        ? code > _currentVersionCode
        : Version.TryParse(info.VersionName.TrimStart('v'), out var latest) &&
          Version.TryParse(_currentVersionName.TrimStart('v'), out var current) && latest > current;

    private static Uri ValidateManifestUri(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException(AppStrings.Get("upd.manifestUrl"), nameof(manifestUrl));
        return uri;
    }

    private static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException(AppStrings.Get("upd.downloadUrl"));
        return uri;
    }
}
