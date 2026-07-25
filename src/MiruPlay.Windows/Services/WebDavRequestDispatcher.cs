using System.Net;
using System.Threading.Channels;

namespace MiruPlay.Windows.Services;

internal enum WebDavRequestKind
{
    PropFind,
    LibraryDatabase,
    Artwork,
    ArtworkPack,
    Head,
    Range,
    Playback,
    Scanner,
}

internal sealed class WebDavCircuitOpenException : HttpRequestException
{
    public WebDavCircuitOpenException(Uri endpoint, DateTimeOffset retryAfter)
        : base($"WebDAV endpoint {endpoint.GetLeftPart(UriPartial.Authority)} is temporarily blocked until {retryAfter:O}.")
    {
        RetryAfter = retryAfter;
    }

    public DateTimeOffset RetryAfter { get; }
}

internal sealed class WebDavResponseLease : IDisposable, IAsyncDisposable
{
    private readonly Action _release;
    private int _disposed;

    public WebDavResponseLease(HttpResponseMessage response, Action release)
    {
        Response = response;
        _release = release;
    }

    public HttpResponseMessage Response { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Response.Dispose();
        _release();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WebDavRequestDispatcher : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _minimumInterval;
    private readonly TimeSpan _initialCooldown;
    private readonly TimeSpan _maximumCooldown;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, EndpointConsumer> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    public WebDavRequestDispatcher(
        HttpMessageHandler handler,
        TimeSpan? minimumInterval = null,
        TimeSpan? initialCooldown = null,
        TimeSpan? maximumCooldown = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(250);
        _initialCooldown = initialCooldown ?? TimeSpan.FromMinutes(2);
        _maximumCooldown = maximumCooldown ?? TimeSpan.FromMinutes(30);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<WebDavResponseLease> SendAsync(
        Uri endpointRoot,
        WebDavRequestKind kind,
        HttpRequestMessage request,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = endpointRoot.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        EndpointConsumer consumer;
        lock (_sync)
        {
            if (!_consumers.TryGetValue(key, out consumer!))
            {
                consumer = new EndpointConsumer(
                    new Uri(key),
                    _client,
                    _minimumInterval,
                    _initialCooldown,
                    _maximumCooldown,
                    _utcNow);
                _consumers.Add(key, consumer);
            }
        }
        return consumer.EnqueueAsync(kind, request, deadline, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        List<EndpointConsumer> consumers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            consumers = _consumers.Values.ToList();
            _consumers.Clear();
        }
        foreach (var consumer in consumers) await consumer.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private sealed class EndpointConsumer : IAsyncDisposable
    {
        private readonly Uri _endpoint;
        private readonly HttpClient _client;
        private readonly TimeSpan _minimumInterval;
        private readonly TimeSpan _initialCooldown;
        private readonly TimeSpan _maximumCooldown;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Channel<WebDavRequest> _queue = Channel.CreateBounded<WebDavRequest>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _consumer;
        private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;
        private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
        private int _consecutiveBans;

        public EndpointConsumer(
            Uri endpoint,
            HttpClient client,
            TimeSpan minimumInterval,
            TimeSpan initialCooldown,
            TimeSpan maximumCooldown,
            Func<DateTimeOffset> utcNow)
        {
            _endpoint = endpoint;
            _client = client;
            _minimumInterval = minimumInterval;
            _initialCooldown = initialCooldown;
            _maximumCooldown = maximumCooldown;
            _utcNow = utcNow;
            _consumer = ConsumeAsync();
        }

        public async Task<WebDavResponseLease> EnqueueAsync(
            WebDavRequestKind kind,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            var item = new WebDavRequest(kind, request, deadline, cancellationToken);
            try
            {
                await _queue.Writer.WriteAsync(item, item.Cancellation.Token).ConfigureAwait(false);
                return await item.Completion.Task.ConfigureAwait(false);
            }
            catch
            {
                item.Dispose();
                throw;
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    if (item.Cancellation.IsCancellationRequested)
                    {
                        item.Completion.TrySetCanceled(item.Cancellation.Token);
                        item.Dispose();
                        continue;
                    }

                    var now = _utcNow();
                    if (now < _blockedUntil)
                    {
                        item.Completion.TrySetException(new WebDavCircuitOpenException(_endpoint, _blockedUntil));
                        item.Dispose();
                        continue;
                    }

                    var halfOpenProbe = _blockedUntil != DateTimeOffset.MinValue;
                    var pacingDelay = _minimumInterval - (now - _lastStarted);
                    if (pacingDelay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(pacingDelay, item.Cancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            if (halfOpenProbe) ReopenAfterFailedProbe();
                            item.Completion.TrySetCanceled(item.Cancellation.Token);
                            item.Dispose();
                            continue;
                        }
                    }

                    try
                    {
                        _lastStarted = _utcNow();
                        var response = await _client.SendAsync(
                            item.Request,
                            HttpCompletionOption.ResponseHeadersRead,
                            item.Cancellation.Token).ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                        {
                            response.Dispose();
                            OpenCircuit();
                            item.Completion.TrySetException(new WebDavCircuitOpenException(_endpoint, _blockedUntil));
                            item.Dispose();
                            FailQueuedWork();
                            continue;
                        }

                        _consecutiveBans = 0;
                        _blockedUntil = DateTimeOffset.MinValue;
                        var lease = new WebDavResponseLease(response, () => item.Release.TrySetResult());
                        if (!item.Completion.TrySetResult(lease)) lease.Dispose();
                        await item.Release.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested)
                    {
                        if (halfOpenProbe) ReopenAfterFailedProbe();
                        item.Completion.TrySetCanceled(item.Cancellation.Token);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        item.Completion.TrySetException(new ObjectDisposedException(nameof(WebDavRequestDispatcher)));
                    }
                    catch (Exception error)
                    {
                        if (halfOpenProbe) ReopenAfterFailedProbe();
                        item.Completion.TrySetException(error);
                    }
                    finally
                    {
                        item.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                FailQueuedWork(new ObjectDisposedException(nameof(WebDavRequestDispatcher)));
            }
        }

        private void OpenCircuit()
        {
            _consecutiveBans = Math.Min(_consecutiveBans + 1, 16);
            _blockedUntil = _utcNow() + CurrentCooldown();
        }

        private void ReopenAfterFailedProbe() => _blockedUntil = _utcNow() + CurrentCooldown();

        private TimeSpan CurrentCooldown()
        {
            var multiplier = Math.Pow(2, Math.Max(0, _consecutiveBans - 1));
            return TimeSpan.FromTicks(Math.Min(
                (long)(_initialCooldown.Ticks * multiplier),
                _maximumCooldown.Ticks));
        }

        private void FailQueuedWork(Exception? error = null)
        {
            while (_queue.Reader.TryRead(out var queued))
            {
                queued.Completion.TrySetException(error ?? new WebDavCircuitOpenException(_endpoint, _blockedUntil));
                queued.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            await _shutdown.CancelAsync().ConfigureAwait(false);
            try
            {
                await _consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _shutdown.Dispose();
        }
    }

    private sealed class WebDavRequest : IDisposable
    {
        public WebDavRequest(
            WebDavRequestKind kind,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            Kind = kind;
            Request = request;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Cancellation.CancelAfter(deadline);
        }

        public WebDavRequestKind Kind { get; }
        public HttpRequestMessage Request { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<WebDavResponseLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            Request.Dispose();
            Cancellation.Dispose();
        }
    }
}
