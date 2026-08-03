using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MiruPlay.Windows.Services;

public sealed class WebDavDirectoryEnumerator : IDisposable
{
    private const int MaximumDepth = 64;
    private const int MaximumEntries = 100_000;
    private const long MaximumResponseCharacters = 32L * 1024 * 1024;
    private readonly WebDavRequestDispatcher _dispatcher;
    private int _disposed;

    public WebDavDirectoryEnumerator(
        HttpMessageHandler? handler = null,
        TimeSpan? minimumRequestInterval = null)
    {
        _dispatcher = new WebDavRequestDispatcher(
            handler ?? new HttpClientHandler { AllowAutoRedirect = false },
            minimumRequestInterval);
    }

    public async Task ValidateAsync(
        string rootUrl,
        MediaSourceCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var root = WebDavMlipClient.NormalizeRoot(rootUrl);
        var responses = await ListAsync(root, credential, depth: 0, cancellationToken).ConfigureAwait(false);
        if (!responses.Any(response => response.IsCollection && IsSamePath(root, response.Uri)))
            throw new InvalidDataException("WebDAV 地址不是可读取的目录。");
    }

    public async Task<IReadOnlyList<DirectoryFileEntry>> EnumerateAsync(
        string rootUrl,
        MediaSourceCredential? credential,
        IProgress<DirectoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = WebDavMlipClient.NormalizeRoot(rootUrl);
        var pending = new Queue<(Uri Uri, int Depth)>();
        pending.Enqueue((root, 0));
        var files = new List<RemoteFileEntry>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entriesSeen = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            if (!seenDirectories.Add(directory.AbsoluteUri)) continue;
            if (depth > MaximumDepth) throw new InvalidDataException("WebDAV 目录层级超过 64 层限制。");

            foreach (var response in await ListAsync(root, directory, credential, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSamePath(root, response.Uri)) continue;
                if (++entriesSeen > MaximumEntries)
                    throw new InvalidDataException("WebDAV 目录条目数超过 100000 限制。");
                var relativePath = RelativePath(root, response.Uri);
                if (response.IsCollection)
                {
                    if (depth == MaximumDepth) throw new InvalidDataException("WebDAV 目录层级超过 64 层限制。");
                    pending.Enqueue((EnsureDirectoryUri(response.Uri), depth + 1));
                    continue;
                }
                if (!DirectoryLibraryIndex.IsVideoFile(relativePath) && !DirectoryLibraryIndex.IsSubtitleFile(relativePath)) continue;
                if (files.Count >= MaximumEntries) throw new InvalidDataException("WebDAV 目录文件数超过 100000 限制。");
                files.Add(new RemoteFileEntry(relativePath, response.ContentLength ?? 0, response.ModifiedMs));
                progress?.Report(new DirectoryScanProgress(files.Count, files.Count, files.Count(entry => DirectoryLibraryIndex.IsVideoFile(entry.RelativePath))));
            }
        }

        var subtitlePaths = files
            .Where(entry => DirectoryLibraryIndex.IsSubtitleFile(entry.RelativePath))
            .Select(entry => entry.RelativePath)
            .ToList();
        return files
            .Where(entry => DirectoryLibraryIndex.IsVideoFile(entry.RelativePath))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new DirectoryFileEntry(
                entry.RelativePath,
                entry.SizeBytes,
                entry.ModifiedMs,
                FindSubtitlePaths(subtitlePaths, entry.RelativePath)))
            .ToList();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task<List<RemoteResponseEntry>> ListAsync(
        Uri root,
        MediaSourceCredential? credential,
        int depth,
        CancellationToken cancellationToken) =>
        await ListAsync(root, root, credential, cancellationToken, depth).ConfigureAwait(false);

    private async Task<List<RemoteResponseEntry>> ListAsync(
        Uri root,
        Uri directory,
        MediaSourceCredential? credential,
        CancellationToken cancellationToken,
        int depth = 1)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), directory);
        request.Headers.UserAgent.ParseAdd("MiruPlay-Windows/1.0");
        request.Headers.Add("Depth", depth == 0 ? "0" : "1");
        if (credential is { IsEmpty: false })
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }
        await using var lease = await _dispatcher.SendAsync(
            root,
            WebDavRequestKind.Scanner,
            request,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        if (lease.Response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("WebDAV 用户名或密码不正确。");
        lease.Response.EnsureSuccessStatusCode();
        await using var stream = await lease.Response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseResponses(root, directory, stream);
        }
        catch (XmlException error)
        {
            throw new InvalidDataException("WebDAV 目录响应不是有效 XML。", error);
        }
    }

    private static List<RemoteResponseEntry> ParseResponses(Uri root, Uri requestUri, Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumResponseCharacters,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(stream, settings);
        var entries = new List<RemoteResponseEntry>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "response") continue;
            using var subtree = reader.ReadSubtree();
            var response = XElement.Load(subtree, LoadOptions.None);
            var href = response.Elements().FirstOrDefault(element => element.Name.LocalName == "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            var uri = ParseResponseUri(root, requestUri, href);
            var prop = response.Descendants().FirstOrDefault(element => element.Name.LocalName == "prop") ?? response;
            var resourceType = prop.Descendants().FirstOrDefault(element => element.Name.LocalName == "resourcetype");
            var collection = resourceType?.Elements().Any(element => element.Name.LocalName == "collection") == true;
            var size = prop.Descendants().FirstOrDefault(element => element.Name.LocalName == "getcontentlength")?.Value;
            var modified = prop.Descendants().FirstOrDefault(element => element.Name.LocalName == "getlastmodified")?.Value;
            if (entries.Count >= MaximumEntries)
                throw new InvalidDataException("WebDAV 目录响应条目数超过 100000 限制。");
            entries.Add(new RemoteResponseEntry(
                uri,
                collection,
                long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : null,
                DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    ? timestamp.ToUnixTimeMilliseconds()
                    : 0));
        }
        return entries;
    }

    private static Uri ParseResponseUri(Uri root, Uri requestUri, string href)
    {
        if (!Uri.TryCreate(requestUri, href.Trim(), out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !root.IsBaseOf(uri))
        {
            throw new InvalidDataException("WebDAV 目录响应包含来源之外的路径。");
        }
        _ = RelativePath(root, uri);
        return uri;
    }

    private static string RelativePath(Uri root, Uri uri)
    {
        var rootSegments = SafeUriSegments(root.AbsolutePath);
        var segments = SafeUriSegments(uri.AbsolutePath);
        if (segments.Count < rootSegments.Count ||
            !segments.Take(rootSegments.Count).SequenceEqual(rootSegments, StringComparer.Ordinal))
            throw new InvalidDataException("WebDAV 目录响应包含来源之外的路径。");
        var relative = string.Join('/', segments.Skip(rootSegments.Count));
        return relative;
    }

    private static List<string> SafeUriSegments(string path)
    {
        var result = new List<string>();
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = Uri.UnescapeDataString(raw);
            if (segment is "." or ".." || segment.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
                throw new InvalidDataException("WebDAV 目录响应包含不安全的路径段。");
            result.Add(segment);
        }
        return result;
    }

    private static bool IsSamePath(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    private static Uri EnsureDirectoryUri(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/");

    private static List<string> FindSubtitlePaths(IEnumerable<string> subtitles, string mediaPath)
    {
        var cleanMedia = mediaPath.Replace('\\', '/');
        var slash = cleanMedia.LastIndexOf('/');
        var directory = slash >= 0 ? cleanMedia[..slash] : "";
        var mediaName = slash >= 0 ? cleanMedia[(slash + 1)..] : cleanMedia;
        var extensionStart = mediaName.LastIndexOf('.');
        var stem = extensionStart > 0 ? mediaName[..extensionStart] : mediaName;
        var prefix = $"{stem}.";
        return subtitles
            .Where(path =>
            {
                var clean = path.Replace('\\', '/');
                var pathSlash = clean.LastIndexOf('/');
                var pathDirectory = pathSlash >= 0 ? clean[..pathSlash] : "";
                var fileName = pathSlash >= 0 ? clean[(pathSlash + 1)..] : clean;
                return pathDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record RemoteFileEntry(string RelativePath, long SizeBytes, long ModifiedMs);
    private sealed record RemoteResponseEntry(Uri Uri, bool IsCollection, long? ContentLength, long ModifiedMs);
}
