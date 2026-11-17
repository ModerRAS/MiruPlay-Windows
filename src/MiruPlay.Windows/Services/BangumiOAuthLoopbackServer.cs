using System.Net;
using System.Text;

namespace MiruPlay.Windows.Services;

/// <summary>Loopback callback endpoint for the Bangumi OAuth redirect.</summary>
public interface IBangumiOAuthLoopback : IDisposable
{
    string Prefix { get; }
    void Start();
    Task<string> WaitForCallbackAsync(CancellationToken cancellationToken);
    void Close();
}

/// <summary>
/// Minimal HttpListener bound to http://127.0.0.1:{port}/ only. The callback never leaves
/// the machine and never traverses the outbound proxy (inbound listener, no HttpClient).
/// </summary>
public sealed class BangumiOAuthLoopbackServer : IBangumiOAuthLoopback
{
    private const string ResultPage =
        "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>MiruPlay 授权</title></head>" +
        "<body style=\"font-family:'Segoe UI',sans-serif;padding:32px\">" +
        "<h1>授权成功，请返回 MiruPlay 客户端</h1><p>可以关闭此浏览器标签页。</p></body></html>";

    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly string _path;
    private readonly TaskCompletionSource<string> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _acceptCancellation;
    private Task? _acceptLoop;
    private int _started;
    private int _stopped;

    public BangumiOAuthLoopbackServer(int port, string path = BangumiOAuthConstants.CallbackPath)
    {
        if (port is < 1 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException("callback path must start with '/'", nameof(path));
        _port = port;
        _path = path;
        _listener.Prefixes.Add(Prefix);
    }

    public string Prefix => $"http://127.0.0.1:{_port}/";

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException(AppStrings.Get("bgmOauth.loopbackStarted"));
        try
        {
            _listener.Start();
        }
        catch (Exception error) when (error is HttpListenerException or System.ComponentModel.Win32Exception)
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException(AppStrings.Get("bgmOauth.portBusy", _port), error);
        }
        _acceptCancellation = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_acceptCancellation.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleContextAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath ?? string.Empty;
            if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(path, _path, StringComparison.Ordinal))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteAsync(response, "Not found", "text/plain; charset=utf-8").ConfigureAwait(false);
                return;
            }
            var callbackUrl = $"http://127.0.0.1:{_port}{_path}{request.Url?.Query}";
            _callback.TrySetResult(callbackUrl);
            response.StatusCode = (int)HttpStatusCode.OK;
            await WriteAsync(response, ResultPage, "text/html; charset=utf-8").ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public Task<string> WaitForCallbackAsync(CancellationToken cancellationToken) =>
        _callback.Task.WaitAsync(cancellationToken);

    public void Close()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            _acceptCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _callback.TrySetCanceled();
    }

    public void Dispose()
    {
        Close();
        _acceptCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}
