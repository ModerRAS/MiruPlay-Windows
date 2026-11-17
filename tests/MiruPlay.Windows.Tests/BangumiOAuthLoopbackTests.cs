using System.Net;
using System.Net.Http;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiOAuthLoopbackTests
{
    [Fact]
    public async Task CallbackIsReceivedOnLoopbackAndReturnsChineseResultPage()
    {
        var port = ReservePort();
        using var server = new BangumiOAuthLoopbackServer(port);
        Assert.Equal($"http://127.0.0.1:{port}/", server.Prefix);
        server.Start();
        using var http = new HttpClient();
        var callbackUrl = $"http://127.0.0.1:{port}/callback?code=code-1&state=state-1";

        using var response = await http.GetAsync(callbackUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("授权成功", html, StringComparison.Ordinal);
        Assert.Equal(callbackUrl, await server.WaitForCallbackAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MultipleCallbacksAreQueuedInOrderUntilClosed()
    {
        var port = ReservePort();
        using var server = new BangumiOAuthLoopbackServer(port);
        server.Start();
        using var http = new HttpClient();
        var first = $"http://127.0.0.1:{port}/callback?code=code-1&state=wrong";
        var second = $"http://127.0.0.1:{port}/callback?code=code-1&state=state-1";

        using var firstResponse = await http.GetAsync(first);
        using var secondResponse = await http.GetAsync(second);

        Assert.Equal(first, await server.WaitForCallbackAsync(CancellationToken.None));
        Assert.Equal(second, await server.WaitForCallbackAsync(CancellationToken.None));
        server.Close();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.WaitForCallbackAsync(cancellation.Token));
    }

    [Fact]
    public async Task UnknownPathReturnsNotFoundAndDoesNotResolveCallback()
    {
        var port = ReservePort();
        using var server = new BangumiOAuthLoopbackServer(port);
        server.Start();
        using var http = new HttpClient();

        using var response = await http.GetAsync($"http://127.0.0.1:{port}/other");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(server.WaitForCallbackAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void PortInUseReportsExplicitForbiddenPortError()
    {
        var port = ReservePort();
        using var first = new BangumiOAuthLoopbackServer(port);
        first.Start();
        using var second = new BangumiOAuthLoopbackServer(port);

        var error = Assert.Throws<InvalidOperationException>(second.Start);

        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains(AppStrings.Get("bgmOauth.portBusy", port), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheServerCancelsThePendingWait()
    {
        var port = ReservePort();
        using var server = new BangumiOAuthLoopbackServer(port);
        server.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = server.WaitForCallbackAsync(cancellation.Token);
        server.Close();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
