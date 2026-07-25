using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

internal sealed class WebDavPlaybackProxy : IAsyncDisposable
{
    private readonly WebDavMlipClient _client;
    private readonly string _rootUrl;
    private readonly MediaSourceCredential? _credential;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Uri> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly Task _server;
    private int _requestId;

    public WebDavPlaybackProxy(
        WebDavMlipClient client,
        string rootUrl,
        MediaSourceCredential? credential,
        LibraryEpisode episode)
    {
        _client = client;
        _rootUrl = WebDavMlipClient.NormalizeRoot(rootUrl).AbsoluteUri;
        _credential = credential;
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Episode = episode with
        {
            MediaPath = AddResource(port, episode.MediaPath),
            SubtitlePaths = episode.SubtitlePaths.Select(path => AddResource(port, path)).ToList(),
        };
        _server = ServeAsync();
    }

    public LibraryEpisode Episode { get; }

    private string AddResource(int port, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return value;
        var root = WebDavMlipClient.NormalizeRoot(_rootUrl);
        if (!root.IsBaseOf(uri)) throw new InvalidDataException("Playback resource does not belong to the WebDAV source.");
        var token = Guid.NewGuid().ToString("N");
        _resources.Add(token, uri);
        return $"http://127.0.0.1:{port}/{token}";
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _requestId);
                var task = HandleAsync(connection, _shutdown.Token);
                _requests[id] = task;
                _ = task.ContinueWith(
                    completed => _requests.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using (connection)
        await using (var stream = connection.GetStream())
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (requestLine is null) return;
                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0] is not ("GET" or "HEAD"))
                {
                    await WriteErrorAsync(stream, 405, "Method Not Allowed", cancellationToken).ConfigureAwait(false);
                    return;
                }

                RangeHeaderValue? range = null;
                var headerBytes = requestLine.Length;
                while (true)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line)) break;
                    headerBytes += line.Length;
                    if (headerBytes > 32 * 1024)
                    {
                        await WriteErrorAsync(stream, 431, "Request Header Fields Too Large", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase) &&
                        !RangeHeaderValue.TryParse(line["Range:".Length..].Trim(), out range))
                    {
                        await WriteErrorAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                var token = parts[1].TrimStart('/').Split(['?', '#'], 2)[0];
                if (!_resources.TryGetValue(token, out var resource))
                {
                    await WriteErrorAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var method = parts[0] == "HEAD" ? HttpMethod.Head : HttpMethod.Get;
                await using var lease = await _client.OpenPlaybackAsync(
                    _rootUrl,
                    resource,
                    _credential,
                    method,
                    range,
                    cancellationToken).ConfigureAwait(false);
                var response = lease.Response;
                await WriteResponseHeadersAsync(stream, response, cancellationToken).ConfigureAwait(false);
                if (method != HttpMethod.Head)
                {
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            {
                // mpv routinely closes range connections while seeking; disposal releases the endpoint lease.
            }
        }
    }

    private static async Task WriteResponseHeadersAsync(
        Stream stream,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append((int)response.StatusCode)
            .Append(' ')
            .Append(response.ReasonPhrase ?? response.StatusCode.ToString())
            .Append("\r\nConnection: close\r\n");
        AppendHeader(builder, "Content-Type", response.Content.Headers.ContentType?.ToString());
        AppendHeader(builder, "Content-Length", response.Content.Headers.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendHeader(builder, "Content-Range", response.Content.Headers.ContentRange?.ToString());
        AppendHeader(builder, "Last-Modified", response.Content.Headers.LastModified?.ToString("R"));
        AppendHeader(builder, "ETag", response.Headers.ETag?.ToString());
        if (response.Headers.AcceptRanges.Count > 0) AppendHeader(builder, "Accept-Ranges", string.Join(", ", response.Headers.AcceptRanges));
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
    }

    private static void AppendHeader(StringBuilder builder, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) builder.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static async Task WriteErrorAsync(
        Stream stream,
        int status,
        string reason,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _server.ConfigureAwait(false);
        var requests = _requests.Values.ToArray();
        if (requests.Length > 0)
        {
            try { await Task.WhenAll(requests).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }
}
