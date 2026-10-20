using System.Collections.Concurrent;
using System.Text.Json;

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
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly string? _storePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly int _maxRetainedTasks;
    private Task? _disposeTask;
    private bool _disposed;

    public HeadlessTaskScheduler(int maxRetainedTasks = 64, string? storePath = null)
    {
        if (maxRetainedTasks is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maxRetainedTasks));
        _maxRetainedTasks = maxRetainedTasks;
        _storePath = storePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "tasks.json");
        if (File.Exists(_storePath))
        {
            try
            {
                var restored = JsonSerializer.Deserialize<List<HeadlessTaskStatus>>(File.ReadAllText(_storePath), _jsonOptions) ?? [];
                foreach (var task in restored.OrderByDescending(item => item.StartedAt).Take(_maxRetainedTasks).Reverse())
                {
                    var recovered = task.State == "RUNNING"
                        ? task with { State = "FAILED", FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Error = "应用关闭时任务中断。" }
                        : task;
                    _tasks[recovered.Id] = recovered;
                }
                TrimHistory();
            }
            catch (JsonException)
            {
                File.Move(_storePath, $"{_storePath}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.invalid", true);
            }
        }
        Persist();
    }

    public bool IsPersistent => _storePath is not null;

    public IReadOnlyList<HeadlessTaskStatus> List() =>
        _tasks.Values.OrderByDescending(task => task.StartedAt).Take(_maxRetainedTasks).ToList();

    public HeadlessTaskStatus? Get(string id) => _tasks.TryGetValue(id, out var task) ? task : null;

    public bool TryStart(
        string id,
        string title,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        TryStart(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), id, title, operation, cancellationToken);

    private bool TryStart(
        TaskCompletionSource completion,
        string id,
        string title,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("任务 ID 不能为空。", nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("任务标题不能为空。", nameof(title));
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var started = new HeadlessTaskStatus(id, title.Trim(), "RUNNING", StartedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!_running.TryAdd(id, Task.CompletedTask)) return false;
            _tasks[id] = started;
            try
            {
                Persist();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _tasks[id] = started with
                {
                    State = "FAILED",
                    FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Error = "无法持久化任务状态。",
                };
                _running.TryRemove(id, out _);
                completion.TrySetResult();
                throw new InvalidOperationException("无法持久化任务状态，任务未启动。", error);
            }

            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellations[id] = linkedCancellation;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await RunAsync(id, operation, linkedCancellation.Token).ConfigureAwait(false);
            }, CancellationToken.None);
            _running[id] = worker;
            start.SetResult();
            _ = worker.ContinueWith(
                completed =>
                {
                    _running.TryRemove(new KeyValuePair<string, Task>(id, completed));
                    completion.TrySetResult();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
    }

    public async Task<bool> RunTrackedAsync(
        string id,
        string title,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryStart(completion, id, title, operation, cancellationToken)) return false;
        await completion.Task.ConfigureAwait(false);
        return Get(id)?.State == "SUCCEEDED";
    }

    public bool Cancel(string id)
    {
        if (!_cancellations.TryGetValue(id, out var cancellation)) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Update(string id, string? message = null, int? progress = null)
    {
        while (_tasks.TryGetValue(id, out var current) && current.State == "RUNNING")
        {
            var updated = current with { Message = message, Progress = progress is null ? null : Math.Clamp(progress.Value, 0, 100) };
            if (!_tasks.TryUpdate(id, updated, current)) continue;
            Persist();
            return;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelRunningTasks();
        }
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            _disposed = true;
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await CancelRunningTasksAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_running.Values).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void CancelRunningTasks()
    {
        foreach (var cancellation in _cancellations.Values)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task CancelRunningTasksAsync()
    {
        foreach (var cancellation in _cancellations.Values)
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RunAsync(string id, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            SetTerminalStatus(id, task => task with
            {
                State = "SUCCEEDED",
                FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Progress = 100,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetTerminalStatus(id, task => task with { State = "CANCELED", FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }
        catch (Exception error)
        {
            SetTerminalStatus(id, task => task with
            {
                State = "FAILED",
                FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = RotatingLocalLogStore.Redact(error.Message),
            });
        }
        finally
        {
            if (_cancellations.TryRemove(id, out var cancellation)) cancellation.Dispose();
            TrimHistory();
            try
            {
                Persist();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                SetTerminalStatus(id, task => task with
                {
                    State = "FAILED",
                    FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Error = "无法持久化任务终态。",
                });
            }
        }
    }

    private void SetTerminalStatus(string id, Func<HeadlessTaskStatus, HeadlessTaskStatus> update)
    {
        while (_tasks.TryGetValue(id, out var current))
        {
            var terminal = update(current);
            if (_tasks.TryUpdate(id, terminal, current)) return;
        }
    }

    private void Persist()
    {
        if (_storePath is null) return;
        _persistenceLock.Wait();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_storePath))!;
            Directory.CreateDirectory(directory);
            var temporary = $"{_storePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(_tasks.Values.OrderByDescending(task => task.StartedAt)
                    .Take(_maxRetainedTasks).ToArray(), _jsonOptions));
                File.Move(temporary, _storePath, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            _persistenceLock.Release();
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
