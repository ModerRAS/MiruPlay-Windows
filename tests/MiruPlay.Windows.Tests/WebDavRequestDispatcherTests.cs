using System.Net;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class WebDavRequestDispatcherTests
{
    [Fact]
    public async Task StreamingLeasePreventsAllTypedRequestsFromOverlapping()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        });
        await using var dispatcher = new WebDavRequestDispatcher(
            handler,
            minimumInterval: TimeSpan.Zero,
            initialCooldown: TimeSpan.FromMilliseconds(50));
        var root = new Uri("https://example.test/dav/");

        var first = await dispatcher.SendAsync(
            root,
            WebDavRequestKind.Playback,
            new HttpRequestMessage(HttpMethod.Get, new Uri(root, "video.mkv")),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var queued = Enum.GetValues<WebDavRequestKind>()
            .Select((kind, index) => dispatcher.SendAsync(
                root,
                kind,
                new HttpRequestMessage(HttpMethod.Get, new Uri(root, $"item-{index}")),
                TimeSpan.FromSeconds(5),
                CancellationToken.None))
            .ToList();

        await Task.Delay(50);
        Assert.Equal(1, handler.RequestCount);
        await first.DisposeAsync();
        foreach (var pending in queued)
        {
            var lease = await pending;
            await lease.DisposeAsync();
        }

        Assert.Equal(1 + queued.Count, handler.RequestCount);
        Assert.Equal(1, handler.MaximumActive);
    }

    [Fact]
    public async Task MethodNotAllowedOpensEndpointCircuitAndAllowsOneHalfOpenProbe()
    {
        var handler = new CountingHandler(index => new HttpResponseMessage(
            index == 1 ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK));
        await using var dispatcher = new WebDavRequestDispatcher(
            handler,
            minimumInterval: TimeSpan.Zero,
            initialCooldown: TimeSpan.FromMilliseconds(500),
            maximumCooldown: TimeSpan.FromSeconds(2));
        var root = new Uri("https://example.test/dav/");

        await Assert.ThrowsAsync<WebDavCircuitOpenException>(() => SendAndDisposeAsync(dispatcher, root, "first"));
        await Assert.ThrowsAsync<WebDavCircuitOpenException>(() => SendAndDisposeAsync(dispatcher, root, "blocked"));
        Assert.Equal(1, handler.RequestCount);

        await Task.Delay(650);
        var probe = dispatcher.SendAsync(
            root,
            WebDavRequestKind.Scanner,
            new HttpRequestMessage(HttpMethod.Get, new Uri(root, "probe")),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var queued = dispatcher.SendAsync(
            root,
            WebDavRequestKind.Artwork,
            new HttpRequestMessage(HttpMethod.Get, new Uri(root, "after-probe")),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var probeLease = await probe;
        await Task.Delay(30);
        Assert.Equal(2, handler.RequestCount);
        await probeLease.DisposeAsync();
        await (await queued).DisposeAsync();

        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task SecondMethodNotAllowedReopensCircuitWithoutImmediateRetry()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        await using var dispatcher = new WebDavRequestDispatcher(
            handler,
            minimumInterval: TimeSpan.Zero,
            initialCooldown: TimeSpan.FromMilliseconds(50),
            maximumCooldown: TimeSpan.FromMilliseconds(200));
        var root = new Uri("https://example.test/dav/");

        await Assert.ThrowsAsync<WebDavCircuitOpenException>(() => SendAndDisposeAsync(dispatcher, root, "first"));
        await Task.Delay(80);
        await Assert.ThrowsAsync<WebDavCircuitOpenException>(() => SendAndDisposeAsync(dispatcher, root, "probe"));
        await Assert.ThrowsAsync<WebDavCircuitOpenException>(() => SendAndDisposeAsync(dispatcher, root, "blocked-again"));

        Assert.Equal(2, handler.RequestCount);
    }

    private static async Task SendAndDisposeAsync(WebDavRequestDispatcher dispatcher, Uri root, string path)
    {
        await using var lease = await dispatcher.SendAsync(
            root,
            WebDavRequestKind.LibraryDatabase,
            new HttpRequestMessage(HttpMethod.Get, new Uri(root, path)),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
    }

    private sealed class CountingHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _active;
        private int _count;
        private int _maximumActive;

        public int RequestCount => Volatile.Read(ref _count);
        public int MaximumActive => Volatile.Read(ref _maximumActive);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximumActive, active);
            var index = Interlocked.Increment(ref _count);
            try
            {
                await Task.Delay(5, cancellationToken);
                return response(index);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }
    }
}
