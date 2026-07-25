using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly WebDavRequestDispatcher _dispatcher;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, Task<string>> _artworkDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _packDownloads = new(StringComparer.Ordinal);
    private int _disposed;

    public WebDavMlipClient(
        HttpMessageHandler? handler = null,
        string? cacheRoot = null,
        TimeSpan? minimumRequestInterval = null,
        TimeSpan? initialCircuitCooldown = null,
        TimeSpan? maximumCircuitCooldown = null)
    {
        _dispatcher = new WebDavRequestDispatcher(
            handler ?? new HttpClientHandler { AllowAutoRedirect = false },
            minimumRequestInterval,
            initialCircuitCooldown,
            maximumCircuitCooldown);
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
        await using var lease = await SendAsync(
            normalizedRoot,
            WebDavRequestKind.LibraryDatabase,
            CreateRequest(HttpMethod.Get, libraryUri, credential),
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        var response = lease.Response;
        ThrowForAuthentication(response);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDatabaseBytes)
            throw new InvalidDataException("远程 library.db 超过 256 MiB 限制。");

        var key = CacheKey(normalizedRoot);
        var targetDirectory = Path.Combine(_cacheRoot, key);
        var stagingDirectory = Path.Combine(_cacheRoot, $".{key}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, "library.db");
        LibraryCatalog catalog;
        string cachePath;
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

            catalog = MlipLibraryReader.LoadRemote(stagingPath, normalizedRoot.AbsoluteUri);
            Directory.CreateDirectory(targetDirectory);
            cachePath = Path.Combine(targetDirectory, "library.db");
            File.Move(stagingPath, cachePath, true);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
        await lease.DisposeAsync().ConfigureAwait(false);

        if (catalog.SchemaVersion >= 4)
        {
            try
            {
                await CacheArtworkPacksAsync(normalizedRoot, credential, catalog, targetDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Pack failures retain existing artwork and the new media/text catalog.
                Debug.WriteLine($"MLIP v4 artwork pack cache failed: {error.Message}");
            }
        }

        return new RemoteMlipSnapshot(
            catalog.SchemaVersion,
            catalog.Series.Count,
            catalog.Series.Sum(series => series.Episodes.Count),
            cachePath);
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

        var task = _artworkDownloads.GetOrAdd(
            cachePath,
            _ => DownloadArtworkCoreAsync(normalizedRoot, artworkUri, credential, cachePath, key, cancellationToken));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted) _artworkDownloads.TryRemove(cachePath, out _);
        }
    }

    internal Task<WebDavResponseLease> OpenPlaybackAsync(
        string rootUrl,
        Uri resource,
        MediaSourceCredential? credential,
        HttpMethod method,
        RangeHeaderValue? range,
        CancellationToken cancellationToken)
    {
        var root = NormalizeRoot(rootUrl);
        if (resource.Scheme is not ("http" or "https") || !root.IsBaseOf(resource))
            throw new InvalidDataException("Playback resource does not belong to the WebDAV source.");
        var request = CreateRequest(method, resource, credential);
        request.Headers.Range = range;
        var kind = method == HttpMethod.Head
            ? WebDavRequestKind.Head
            : range is null ? WebDavRequestKind.Playback : WebDavRequestKind.Range;
        return SendAsync(root, kind, request, TimeSpan.FromHours(12), cancellationToken);
    }

    public void DeleteCache(string rootUrl)
    {
        var normalizedRoot = NormalizeRoot(rootUrl);
        var directory = Path.Combine(_cacheRoot, CacheKey(normalizedRoot));
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public static Uri NormalizeRoot(string rootUrl)
    {
        if (!Uri.TryCreate(rootUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("WebDAV 地址必须是绝对 HTTP 或 HTTPS URL。", nameof(rootUrl));
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("WebDAV 地址不能包含嵌入凭据、查询或片段。", nameof(rootUrl));
        var builder = new UriBuilder(uri) { Path = $"{uri.AbsolutePath.TrimEnd('/')}/" };
        return builder.Uri;
    }

    private async Task CacheArtworkPacksAsync(
        Uri root,
        MediaSourceCredential? credential,
        LibraryCatalog catalog,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        using (var request = CreateRequest(new HttpMethod("PROPFIND"), new Uri(root, "MLIP-Artwork/"), credential))
        {
            request.Headers.Add("Depth", "1");
            await using var listing = await SendAsync(
                root,
                WebDavRequestKind.PropFind,
                request,
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            ThrowForAuthentication(listing.Response);
            listing.Response.EnsureSuccessStatusCode();
        }

        var neededPackIds = catalog.ArtworkBindings
            .Select(binding => binding.Reference?.Asset.PackId)
            .OfType<long>()
            .ToHashSet();
        var cache = new ArtworkPackCache(targetDirectory);
        foreach (var pack in catalog.ArtworkPacks.Where(pack => neededPackIds.Contains(pack.Id)).OrderBy(pack => pack.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloadKey = $"{targetDirectory}|{pack.Sha256}";
            var task = _packDownloads.GetOrAdd(
                downloadKey,
                _ => DownloadPackAsync(root, credential, pack, cache, cancellationToken));
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (task.IsCompleted) _packDownloads.TryRemove(downloadKey, out _);
            }
        }
    }

    private async Task DownloadPackAsync(
        Uri root,
        MediaSourceCredential? credential,
        MlipArtworkPack pack,
        ArtworkPackCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.IsComplete(pack)) return;
        var packUri = new Uri(root, string.Join('/', pack.Path.Replace('\\', '/').TrimStart('/').Split('/').Select(Uri.EscapeDataString)));
        await using var lease = await SendAsync(
            root,
            WebDavRequestKind.ArtworkPack,
            CreateRequest(HttpMethod.Get, packUri, credential),
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);
        ThrowForAuthentication(lease.Response);
        lease.Response.EnsureSuccessStatusCode();
        if (lease.Response.Content.Headers.ContentLength is long length && length != pack.ByteSize)
            throw new InvalidDataException("MLIP artwork pack HTTP length does not match its catalog.");
        await using var input = await lease.Response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await cache.ExtractAsync(pack, input, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadArtworkCoreAsync(
        Uri normalizedRoot,
        Uri artworkUri,
        MediaSourceCredential? credential,
        string cachePath,
        string key,
        CancellationToken cancellationToken)
    {
        await using var lease = await SendAsync(
            normalizedRoot,
            WebDavRequestKind.Artwork,
            CreateRequest(HttpMethod.Get, artworkUri, credential),
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        ThrowForAuthentication(lease.Response);
        lease.Response.EnsureSuccessStatusCode();
        if (lease.Response.Content.Headers.ContentLength is > MaximumArtworkBytes)
            throw new InvalidDataException("远程海报超过 20 MiB 限制。");

        var stagingPath = Path.Combine(Path.GetDirectoryName(cachePath)!, $".{key}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = await lease.Response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
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
            if (File.Exists(cachePath)) File.Delete(stagingPath);
            else File.Move(stagingPath, cachePath);
            return cachePath;
        }
        finally
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private Task<WebDavResponseLease> SendAsync(
        Uri root,
        WebDavRequestKind kind,
        HttpRequestMessage request,
        TimeSpan deadline,
        CancellationToken cancellationToken) =>
        _dispatcher.SendAsync(root, kind, request, deadline, cancellationToken);

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, MediaSourceCredential? credential)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("MiruPlay-Windows/1.0");
        if (credential is { IsEmpty: false })
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }
        return request;
    }

    private static void ThrowForAuthentication(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("WebDAV 用户名或密码不正确。");
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
