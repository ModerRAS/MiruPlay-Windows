using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record RemoteMlipSnapshot(int SchemaVersion, int SeriesCount, int EpisodeCount, string CachePath);

public sealed class WebDavMlipClient : IDisposable
{
    private const long MaximumDatabaseBytes = 256L * 1024 * 1024;
    private const long MaximumArtworkBytes = 20L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;

    public WebDavMlipClient(HttpMessageHandler? handler = null, string? cacheRoot = null)
    {
        _httpClient = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "source-cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<RemoteMlipSnapshot> DownloadAndValidateAsync(
        string rootUrl,
        MediaSourceCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizeRoot(rootUrl);
        var libraryUri = new Uri(normalizedRoot, "library.db");
        using var request = new HttpRequestMessage(HttpMethod.Get, libraryUri);
        request.Headers.UserAgent.ParseAdd("MiruPlay-Windows/1.0");
        if (credential is { IsEmpty: false })
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("WebDAV 用户名或密码不正确。");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDatabaseBytes)
        {
            throw new InvalidDataException("远程 library.db 超过 256 MiB 限制。");
        }

        var key = CacheKey(normalizedRoot);
        var targetDirectory = Path.Combine(_cacheRoot, key);
        var stagingDirectory = Path.Combine(_cacheRoot, $".{key}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, "library.db");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyBoundedAsync(input, output, MaximumDatabaseBytes, "远程 library.db 超过 256 MiB 限制。", cancellationToken).ConfigureAwait(false);
            }

            var catalog = MlipLibraryReader.LoadRemote(stagingPath, normalizedRoot.AbsoluteUri);
            Directory.CreateDirectory(targetDirectory);
            var cachePath = Path.Combine(targetDirectory, "library.db");
            File.Move(stagingPath, cachePath, true);
            return new RemoteMlipSnapshot(
                catalog.SchemaVersion,
                catalog.Series.Count,
                catalog.Series.Sum(series => series.Episodes.Count),
                cachePath);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
    }

    public LibraryCatalog LoadCachedCatalog(string rootUrl)
    {
        var normalizedRoot = NormalizeRoot(rootUrl);
        var cachePath = Path.Combine(_cacheRoot, CacheKey(normalizedRoot), "library.db");
        return MlipLibraryReader.LoadRemote(cachePath, normalizedRoot.AbsoluteUri);
    }

    public async Task<string> DownloadArtworkAsync(
        string rootUrl,
        string artworkUrl,
        MediaSourceCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizeRoot(rootUrl);
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUri) ||
            artworkUri.Scheme is not ("http" or "https") ||
            !normalizedRoot.IsBaseOf(artworkUri))
        {
            throw new InvalidDataException("远程海报地址不属于当前 WebDAV 媒体源。");
        }

        var artworkDirectory = Path.Combine(_cacheRoot, CacheKey(normalizedRoot), "artwork");
        Directory.CreateDirectory(artworkDirectory);
        var extension = Path.GetExtension(artworkUri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".bmp" or ".gif" or ".jpeg" or ".jpg" or ".png" or ".tif" or ".tiff")) extension = ".image";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artworkUri.AbsoluteUri)))[..32];
        var cachePath = Path.Combine(artworkDirectory, $"{key}{extension}");
        if (File.Exists(cachePath)) return cachePath;

        using var request = new HttpRequestMessage(HttpMethod.Get, artworkUri);
        request.Headers.UserAgent.ParseAdd("MiruPlay-Windows/1.0");
        if (credential is { IsEmpty: false })
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("WebDAV 用户名或密码不正确。");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
        {
            throw new InvalidDataException("远程海报超过 20 MiB 限制。");
        }

        var stagingPath = Path.Combine(artworkDirectory, $".{key}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyBoundedAsync(input, output, MaximumArtworkBytes, "远程海报超过 20 MiB 限制。", cancellationToken).ConfigureAwait(false);
            }
            File.Move(stagingPath, cachePath, false);
            return cachePath;
        }
        finally
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    public void DeleteCache(string rootUrl)
    {
        var normalizedRoot = NormalizeRoot(rootUrl);
        var directory = Path.Combine(_cacheRoot, CacheKey(normalizedRoot));
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    public static Uri NormalizeRoot(string rootUrl)
    {
        if (!Uri.TryCreate(rootUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("WebDAV 地址必须是绝对 HTTP 或 HTTPS URL。", nameof(rootUrl));
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("WebDAV 地址不能包含嵌入凭据、查询或片段。", nameof(rootUrl));
        }
        var builder = new UriBuilder(uri);
        builder.Path = $"{builder.Path.TrimEnd('/')}/";
        return builder.Uri;
    }

    private static string CacheKey(Uri normalizedRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot.AbsoluteUri)))[..24];

    private static async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        string limitMessage,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > maximumBytes) throw new InvalidDataException(limitMessage);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
