using System.Collections.Concurrent;

namespace MiruPlay.Windows.Services;

public sealed record HeadlessTaskStatus(
    string Id,
    string Title,
    string State,
    string? Message = null,
    int? Progress = null,
    long StartedAt = 0,
    long? FinishedAt = null,
    string? Error = null);

public sealed class HeadlessTaskScheduler : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HeadlessTaskStatus> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly int _maxRetainedTasks;
    private bool _disposed;

    public HeadlessTaskScheduler(int maxRetainedTasks = 64)
    {
        if (maxRetainedTasks is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maxRetainedTasks));
        _maxRetainedTasks = maxRetainedTasks;
    }

    public IReadOnlyList<HeadlessTaskStatus> List() =>
        _tasks.Values.OrderByDescending(task => task.StartedAt).Take(_maxRetainedTasks).ToList();

    public HeadlessTaskStatus? Get(string id) => _tasks.TryGetValue(id, out var task) ? task : null;

    public bool TryStart(
        string id,
        string title,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("任务 ID 不能为空。", nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("任务标题不能为空。", nameof(title));
        var started = new HeadlessTaskStatus(id, title.Trim(), "RUNNING", StartedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!_running.TryAdd(id, Task.CompletedTask)) return false;
        _tasks[id] = started;
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellations[id] = linkedCancellation;
        var task = RunAsync(id, operation, linkedCancellation.Token);
        _running[id] = task;
        _ = task.ContinueWith(
            completed => _running.TryRemove(new KeyValuePair<string, Task>(id, completed)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    public bool Cancel(string id)
    {
        if (!_cancellations.TryGetValue(id, out var cancellation)) return false;
        cancellation.Cancel();
        return true;
    }

    public void Update(string id, string? message = null, int? progress = null)
    {
        if (!_tasks.TryGetValue(id, out var current) || current.State != "RUNNING") return;
        _tasks[id] = current with { Message = message, Progress = progress is null ? null : Math.Clamp(progress.Value, 0, 100) };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(string id, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var current = _tasks[id];
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            var latest = _tasks.TryGetValue(id, out var currentStatus) ? currentStatus : current;
            _tasks[id] = latest with
            {
                State = "SUCCEEDED",
                FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Progress = 100,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var latest = _tasks.TryGetValue(id, out var currentStatus) ? currentStatus : current;
            _tasks[id] = latest with { State = "CANCELED", FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }
        catch (Exception error)
        {
            var latest = _tasks.TryGetValue(id, out var currentStatus) ? currentStatus : current;
            _tasks[id] = latest with
            {
                State = "FAILED",
                FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = RotatingLocalLogStore.Redact(error.Message),
            };
        }
        finally
        {
            if (_cancellations.TryRemove(id, out var cancellation)) cancellation.Dispose();
            TrimHistory();
        }
    }

    private void TrimHistory()
    {
        foreach (var task in _tasks.Values
                     .Where(item => item.State != "RUNNING")
                     .OrderByDescending(item => item.FinishedAt ?? item.StartedAt)
                     .Skip(_maxRetainedTasks))
            _tasks.TryRemove(task.Id, out _);
    }
}
