using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed class MpvPlaybackSession : IAsyncDisposable
{
    private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly LibraryEpisode _episode;
    private readonly PlaybackProgressStore _progressStore;
    private readonly IAsyncDisposable? _transportLease;
    private readonly SemaphoreSlim _ipcLock = new(1, 1);
    private readonly object _errorSync = new();
    private readonly Queue<string> _errors = new();
    private readonly object _audioSync = new();
    private readonly object _subtitleSync = new();
    private IReadOnlyList<MpvAudioTrack> _audioTracks = [];
    private int? _selectedAudioTrackId;
    private double _speed = 1.0;
    private string? _fileName;
    private string? _mediaTitle;
    private string? _containerFormat;
    private string? _audioCodec;
    private MpvVideoTrackInfo? _video;
    private IReadOnlyList<PlaybackSubtitleTrack> _subtitleTracks = [];
    private int? _selectedSubtitleTrackId;
    private int _requestId;
    private int _ending;
    private int _explicitStopRequested;
    private int _naturalEndObserved;
    private int _activeCommands;
    private long _positionMs;
    private long _durationMs;
    private volatile bool _paused;
    private AudioDspFilterGraph? _appliedAudioDsp;

    private MpvPlaybackSession(
        Process process,
        NamedPipeClientStream pipe,
        LibraryEpisode episode,
        PlaybackProgressStore progressStore,
        IAsyncDisposable? transportLease)
    {
        _process = process;
        _pipe = pipe;
        _reader = new StreamReader(pipe);
        _writer = new StreamWriter(pipe) { AutoFlush = true };
        _episode = episode;
        _progressStore = progressStore;
        _transportLease = transportLease;
        _durationMs = Convert.ToInt64(episode.Duration.TotalMilliseconds, CultureInfo.InvariantCulture);
        _process.ErrorDataReceived += Process_ErrorDataReceived;
        try
        {
            _process.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
            // A process that failed during startup can exit before IPC attaches.
        }
    }

    public Task Completion { get; private set; } = Task.CompletedTask;
    public LibraryEpisode Episode => _episode;
    public bool WasCompleted { get; private set; }
    public long PositionMs => Interlocked.Read(ref _positionMs);
    public long DurationMs => Interlocked.Read(ref _durationMs);
    public bool IsPaused => _paused;
    public string? LastError
    {
        get
        {
            lock (_errorSync) return _errors.Count == 0 ? null : string.Join(Environment.NewLine, _errors);
        }
    }
    public IReadOnlyList<PlaybackSubtitleTrack> SubtitleTracks
    {
        get
        {
            lock (_subtitleSync) return _subtitleTracks.ToArray();
        }
    }
    public int? SelectedSubtitleTrackId
    {
        get
        {
            lock (_subtitleSync) return _selectedSubtitleTrackId;
        }
    }
    public IReadOnlyList<MpvAudioTrack> AudioTracks
    {
        get
        {
            lock (_audioSync) return _audioTracks.ToArray();
        }
    }
    public int? SelectedAudioTrackId
    {
        get
        {
            lock (_audioSync) return _selectedAudioTrackId;
        }
    }
    public double Speed => Volatile.Read(ref _speed);
    public MpvPlaybackInfo PlaybackInfo => BuildPlaybackInfo();
    public AudioDspFilterGraph? AppliedAudioDsp => _appliedAudioDsp;
    public bool IsActive => !Completion.IsCompleted && Volatile.Read(ref _ending) == 0;
    public bool IsPlaying => IsActive && !IsPaused;
    public event EventHandler? SubtitleTracksChanged;
    public event EventHandler? AudioTracksChanged;
    public event EventHandler? PlaybackInfoChanged;

    public async Task SetSubtitleTrackAsync(int? trackId) =>
        await ExecuteCommandAsync(new MpvPlaybackCommand("subtitle", SubtitleTrackId: trackId)).ConfigureAwait(false);

    public async Task SetAudioTrackAsync(int? trackId) =>
        await ExecuteCommandAsync(new MpvPlaybackCommand("audio", AudioTrackId: trackId)).ConfigureAwait(false);

    public Task SeekAsync(long positionMs) =>
        ExecuteCommandAsync(new MpvPlaybackCommand("seek", PositionMs: positionMs));

    public Task SeekRelativeAsync(long deltaMs) =>
        ExecuteCommandAsync(new MpvPlaybackCommand("seek_relative", DeltaMs: deltaMs));

    public Task SetSpeedAsync(float speed) =>
        ExecuteCommandAsync(new MpvPlaybackCommand("speed", Speed: speed));

    public Task TogglePauseAsync() => ExecuteCommandAsync(new MpvPlaybackCommand("toggle"));

    public async Task ApplyAudioDspAsync(AudioDspFilterGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!IsActive) throw new InvalidOperationException("当前没有可控制的 mpv 播放会话。");

        await _ipcLock.WaitAsync(IpcTimeout).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _ending) != 0)
                throw new InvalidOperationException("mpv 播放会话正在结束。");
            await SendRequestLockedAsync(CreateAudioDspCommand(graph)).ConfigureAwait(false);
            _appliedAudioDsp = graph;
        }
        finally
        {
            _ipcLock.Release();
        }
    }

    public async Task<string?> GetAudioFilterGraphAsync()
    {
        if (!IsActive) return null;
        await _ipcLock.WaitAsync(IpcTimeout).ConfigureAwait(false);
        try
        {
            var data = await SendRequestLockedAsync(["get_property", "af"], throwOnError: false).ConfigureAwait(false);
            return data is null
                ? null
                : data.Value.ValueKind == JsonValueKind.String
                    ? data.Value.GetString()
                    : JsonSerializer.Serialize(data.Value);
        }
        finally
        {
            _ipcLock.Release();
        }
    }

    internal static object[] CreateAudioDspCommand(AudioDspFilterGraph graph) =>
        ["set_property", "af", string.IsNullOrWhiteSpace(graph.AfValue) ? Array.Empty<object>() : $"lavfi=[{graph.AfValue}]"];

    public async Task<MpvPlaybackInfo> GetPlaybackInfoAsync()
    {
        if (!IsActive) return PlaybackInfo;
        await _ipcLock.WaitAsync(IpcTimeout).ConfigureAwait(false);
        try
        {
            await RefreshPlaybackInfoLockedAsync().ConfigureAwait(false);
            return BuildPlaybackInfo();
        }
        finally
        {
            _ipcLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsActive)
        {
            try
            {
                await ExecuteCommandAsync(new PlaybackControlCommand("stop")).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException or TimeoutException)
            {
                Interlocked.Exchange(ref _ending, 1);
            }
        }
        await Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public static async Task<MpvPlaybackSession> AttachAsync(
        Process process,
        string pipeName,
        LibraryEpisode episode,
        PlaybackProgressStore progressStore,
        int connectTimeoutMs = 5_000,
        IAsyncDisposable? transportLease = null)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeoutMs).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        {
            pipe.Dispose();
            await TerminateUnattachedProcessAsync(process).ConfigureAwait(false);
            throw new InvalidOperationException("mpv 已启动，但无法建立控制连接。", error);
        }

        var session = new MpvPlaybackSession(process, pipe, episode, progressStore, transportLease);
        session.Completion = session.MonitorAsync();
        return session;
    }

    private static async Task TerminateUnattachedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // The process can exit between the state check and termination.
        }
        finally
        {
            process.Dispose();
        }
    }

    public Task ExecuteCommandAsync(PlaybackControlCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteCommandAsync(new MpvPlaybackCommand(
            request.Command,
            request.PositionMs,
            request.DeltaMs,
            request.Speed,
            request.SubtitleTrackId,
            request.AudioTrackId));
    }

    public async Task ExecuteCommandAsync(MpvPlaybackCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsActive) throw new InvalidOperationException("当前没有可控制的 mpv 播放会话。");

        Interlocked.Increment(ref _activeCommands);
        var stopRequested = false;
        try
        {
            await _ipcLock.WaitAsync(IpcTimeout).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _ending) != 0)
                {
                    throw new InvalidOperationException("mpv 播放会话正在结束。");
                }
                var command = request.Command.Trim().ToLowerInvariant();
                switch (command)
                {
                    case "pause":
                        await SendRequestLockedAsync(["set_property", "pause", true]).ConfigureAwait(false);
                        _paused = true;
                        break;
                    case "resume":
                    case "play":
                        await SendRequestLockedAsync(["set_property", "pause", false]).ConfigureAwait(false);
                        _paused = false;
                        break;
                    case "toggle":
                        await SendRequestLockedAsync(["cycle", "pause"]).ConfigureAwait(false);
                        _paused = await ReadBooleanPropertyLockedAsync("pause").ConfigureAwait(false) ?? !_paused;
                        break;
                    case "stop":
                        Interlocked.Exchange(ref _explicitStopRequested, 1);
                        Interlocked.Exchange(ref _ending, 1);
                        stopRequested = true;
                        try
                        {
                            await SendRequestLockedAsync(["quit"]).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException)
                        {
                            // A successful quit can close IPC before mpv sends its response.
                        }
                        break;
                    case "seek":
                        await SeekLockedAsync(request.PositionMs ?? 0).ConfigureAwait(false);
                        break;
                    case "seek_relative":
                        await SeekLockedAsync(PositionMs + (request.DeltaMs ?? 0)).ConfigureAwait(false);
                        break;
                    case "skip_forward":
                        await SeekLockedAsync(PositionMs + (request.DeltaMs ?? 30_000)).ConfigureAwait(false);
                        break;
                    case "skip_backward":
                        await SeekLockedAsync(PositionMs - (request.DeltaMs ?? 10_000)).ConfigureAwait(false);
                        break;
                    case "speed":
                        var speed = request.Speed ?? 1.0f;
                        if (!float.IsFinite(speed) || speed <= 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(request), "播放速度必须大于 0。");
                        }
                        await SendRequestLockedAsync(["set_property", "speed", speed]).ConfigureAwait(false);
                        Volatile.Write(ref _speed, speed);
                        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                        break;
                    case "subtitle":
                        await SetSubtitleTrackLockedAsync(request.SubtitleTrackId).ConfigureAwait(false);
                        break;
                    case "audio":
                        await SetAudioTrackLockedAsync(request.AudioTrackId).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentException($"未知播放命令: {request.Command}", nameof(request));
                }
            }
            finally
            {
                _ipcLock.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeCommands);
        }

        if (stopRequested)
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync()
    {
        var saveInterval = Stopwatch.StartNew();
        try
        {
            while (Volatile.Read(ref _ending) == 0 && !_process.HasExited)
            {
                await SampleRuntimeStateAsync().ConfigureAwait(false);
                if (Volatile.Read(ref _ending) != 0) break;
                if (saveInterval.Elapsed >= TimeSpan.FromSeconds(15))
                {
                    SaveCurrent(completed: false);
                    saveInterval.Restart();
                }

                var delay = Task.Delay(TimeSpan.FromSeconds(1));
                var exited = _process.WaitForExitAsync();
                if (await Task.WhenAny(delay, exited).ConfigureAwait(false) == exited) break;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            AddError($"mpv IPC: {error.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ending, 1);
            WasCompleted = Volatile.Read(ref _explicitStopRequested) == 0 &&
                (Volatile.Read(ref _naturalEndObserved) != 0 ||
                    DurationMs > 0 && PositionMs >= DurationMs * 0.9);
            SaveCurrent(WasCompleted);
            await CleanupProcessAndPipeAsync().ConfigureAwait(false);
        }
    }

    private async Task SampleRuntimeStateAsync()
    {
        await _ipcLock.WaitAsync(IpcTimeout).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _ending) != 0) return;
            var position = await ReadNumberPropertyLockedAsync("time-pos").ConfigureAwait(false);
            var duration = await ReadNumberPropertyLockedAsync("duration").ConfigureAwait(false);
            var paused = await ReadBooleanPropertyLockedAsync("pause").ConfigureAwait(false);
            var reachedEnd = await ReadBooleanPropertyLockedAsync("eof-reached").ConfigureAwait(false);
            var trackData = await SendRequestLockedAsync(["get_property", "track-list"], throwOnError: false).ConfigureAwait(false);
            var sidData = await SendRequestLockedAsync(["get_property", "sid"], throwOnError: false).ConfigureAwait(false);
            var aidData = await SendRequestLockedAsync(["get_property", "aid"], throwOnError: false).ConfigureAwait(false);
            var speedData = await SendRequestLockedAsync(["get_property", "speed"], throwOnError: false).ConfigureAwait(false);
            if (position is not null) Interlocked.Exchange(ref _positionMs, ToMilliseconds(position.Value));
            if (duration is not null) Interlocked.Exchange(ref _durationMs, ToMilliseconds(duration.Value));
            if (paused is not null) _paused = paused.Value;
            if (speedData is { ValueKind: JsonValueKind.Number } && speedData.Value.TryGetDouble(out var speed) && double.IsFinite(speed))
                Volatile.Write(ref _speed, speed);
            if (trackData is not null)
            {
                UpdateSubtitleState(ParseSubtitleTracks(trackData.Value), ParseSubtitleTrackId(sidData));
                UpdateAudioState(ParseAudioTracks(trackData.Value), ParseAudioTrackId(aidData));
            }
            PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
            if (reachedEnd == true)
            {
                Interlocked.Exchange(ref _naturalEndObserved, 1);
                Interlocked.Exchange(ref _ending, 1);
                try
                {
                    await SendRequestLockedAsync(["quit"]).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException)
                {
                    // A successful quit can close IPC before mpv sends its response.
                }
            }
        }
        finally
        {
            _ipcLock.Release();
        }
    }

    internal static IReadOnlyList<PlaybackSubtitleTrack> ParseSubtitleTracks(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return [];
        var tracks = new List<PlaybackSubtitleTrack>();
        foreach (var item in data.EnumerateArray())
        {
            if (StringProperty(item, "type") != "sub" ||
                !item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id)) continue;
            var externalFileName = StringProperty(item, "external-filename");
            tracks.Add(new PlaybackSubtitleTrack(
                id,
                StringProperty(item, "lang") ?? "und",
                StringProperty(item, "title") ?? string.Empty,
                StringProperty(item, "codec") ?? string.Empty,
                BooleanProperty(item, "external") || !string.IsNullOrWhiteSpace(externalFileName),
                externalFileName,
                BooleanProperty(item, "selected")));
        }
        return tracks;
    }

    internal static IReadOnlyList<MpvAudioTrack> ParseAudioTracks(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray()
            .Where(item => StringProperty(item, "type") == "audio" &&
                item.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out _))
            .Select(item => new MpvAudioTrack(
                item.GetProperty("id").GetInt32(),
                StringProperty(item, "lang") ?? "und",
                StringProperty(item, "title") ?? string.Empty,
                StringProperty(item, "codec") ?? string.Empty,
                BooleanProperty(item, "external"),
                BooleanProperty(item, "selected")))
            .ToArray();
    }

    internal static int? ParseSubtitleTrackId(JsonElement? data)
    {
        if (data is not JsonElement value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id)) return id;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out id) ? id : null;
    }

    internal static int? ParseAudioTrackId(JsonElement? data) => ParseSubtitleTrackId(data);

    private static string? StringProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool BooleanProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task SetSubtitleTrackLockedAsync(int? selectedTrackId)
    {
        if (selectedTrackId is not null && SubtitleTracks.Count == 0)
            await RefreshTracksLockedAsync().ConfigureAwait(false);
        if (selectedTrackId is int id && !SubtitleTracks.Any(track => track.Id == id))
            throw new ArgumentOutOfRangeException(nameof(selectedTrackId), "字幕轨道不存在。");
        await SendRequestLockedAsync(["set_property", "sid", selectedTrackId is int selected ? selected : "no"]).ConfigureAwait(false);
        UpdateSelectedSubtitle(selectedTrackId);
    }

    private async Task SetAudioTrackLockedAsync(int? selectedTrackId)
    {
        if (selectedTrackId is not null && AudioTracks.Count == 0)
            await RefreshTracksLockedAsync().ConfigureAwait(false);
        if (selectedTrackId is int id && !AudioTracks.Any(track => track.Id == id))
            throw new ArgumentOutOfRangeException(nameof(selectedTrackId), "音频轨道不存在。");
        await SendRequestLockedAsync(["set_property", "aid", selectedTrackId is int selected ? selected : "no"]).ConfigureAwait(false);
        UpdateSelectedAudio(selectedTrackId);
    }

    private async Task RefreshTracksLockedAsync()
    {
        var trackData = await SendRequestLockedAsync(["get_property", "track-list"], throwOnError: false).ConfigureAwait(false);
        if (trackData is null) return;
        var sidData = await SendRequestLockedAsync(["get_property", "sid"], throwOnError: false).ConfigureAwait(false);
        var aidData = await SendRequestLockedAsync(["get_property", "aid"], throwOnError: false).ConfigureAwait(false);
        UpdateSubtitleState(ParseSubtitleTracks(trackData.Value), ParseSubtitleTrackId(sidData));
        UpdateAudioState(ParseAudioTracks(trackData.Value), ParseAudioTrackId(aidData));
    }

    private async Task RefreshPlaybackInfoLockedAsync()
    {
        var position = await ReadNumberPropertyLockedAsync("time-pos").ConfigureAwait(false);
        var duration = await ReadNumberPropertyLockedAsync("duration").ConfigureAwait(false);
        var paused = await ReadBooleanPropertyLockedAsync("pause").ConfigureAwait(false);
        var speed = await ReadNumberPropertyLockedAsync("speed").ConfigureAwait(false);
        var fileName = await ReadStringPropertyLockedAsync("filename").ConfigureAwait(false);
        var mediaTitle = await ReadStringPropertyLockedAsync("media-title").ConfigureAwait(false);
        var container = await ReadStringPropertyLockedAsync("file-format").ConfigureAwait(false);
        var audioCodec = await ReadStringPropertyLockedAsync("audio-codec-name").ConfigureAwait(false);
        var videoCodec = await ReadStringPropertyLockedAsync("video-codec").ConfigureAwait(false);
        var videoParams = await SendRequestLockedAsync(["get_property", "video-params"], throwOnError: false).ConfigureAwait(false);
        await RefreshTracksLockedAsync().ConfigureAwait(false);
        if (position is not null) Interlocked.Exchange(ref _positionMs, ToMilliseconds(position.Value));
        if (duration is not null) Interlocked.Exchange(ref _durationMs, ToMilliseconds(duration.Value));
        if (paused is not null) _paused = paused.Value;
        if (speed is not null && double.IsFinite(speed.Value)) Volatile.Write(ref _speed, speed.Value);
        _fileName = fileName;
        _mediaTitle = mediaTitle;
        _containerFormat = container;
        _audioCodec = audioCodec;
        _video = ParseVideoTrackInfo(videoCodec, videoParams);
        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    private static MpvVideoTrackInfo? ParseVideoTrackInfo(string? codec, JsonElement? data)
    {
        if (data is not JsonElement value || value.ValueKind != JsonValueKind.Object) return codec is null ? null : new(codec, null, null, null, null, null);
        return new(
            codec,
            StringProperty(value, "pixelformat"),
            StringProperty(value, "primaries"),
            StringProperty(value, "gamma") ?? StringProperty(value, "transfer"),
            IntProperty(value, "w"),
            IntProperty(value, "h"));
    }

    private static int? IntProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private async Task<string?> ReadStringPropertyLockedAsync(string property)
    {
        var data = await SendRequestLockedAsync(["get_property", property], throwOnError: false).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.String } ? data.Value.GetString() : null;
    }

    private void UpdateSelectedAudio(int? selectedTrackId)
    {
        IReadOnlyList<MpvAudioTrack> tracks;
        lock (_audioSync) tracks = _audioTracks;
        UpdateAudioState(tracks, selectedTrackId);
    }

    private void UpdateAudioState(IReadOnlyList<MpvAudioTrack> tracks, int? selectedTrackId)
    {
        var normalized = tracks.Select(track => track with { IsSelected = track.Id == selectedTrackId }).ToArray();
        bool changed;
        lock (_audioSync)
        {
            changed = _selectedAudioTrackId != selectedTrackId || !_audioTracks.SequenceEqual(normalized);
            _audioTracks = normalized;
            _selectedAudioTrackId = selectedTrackId;
        }
        if (changed) AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private MpvPlaybackInfo BuildPlaybackInfo() => new(
        PositionMs,
        DurationMs,
        IsPaused,
        Speed,
        _fileName,
        _mediaTitle,
        _containerFormat,
        _audioCodec,
        _video,
        AudioTracks,
        SubtitleTracks,
        SelectedAudioTrackId,
        SelectedSubtitleTrackId);

    private void UpdateSelectedSubtitle(int? selectedTrackId)
    {
        IReadOnlyList<PlaybackSubtitleTrack> tracks;
        lock (_subtitleSync) tracks = _subtitleTracks;
        UpdateSubtitleState(tracks, selectedTrackId);
    }

    private void UpdateSubtitleState(IReadOnlyList<PlaybackSubtitleTrack> tracks, int? selectedTrackId)
    {
        var normalized = tracks.Select(track => track with { IsSelected = track.Id == selectedTrackId }).ToArray();
        bool changed;
        lock (_subtitleSync)
        {
            changed = _selectedSubtitleTrackId != selectedTrackId || !_subtitleTracks.SequenceEqual(normalized);
            _subtitleTracks = normalized;
            _selectedSubtitleTrackId = selectedTrackId;
        }
        if (changed) SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SeekLockedAsync(long requestedPositionMs)
    {
        var maximum = DurationMs > 0 ? DurationMs : long.MaxValue;
        var target = Math.Clamp(requestedPositionMs, 0, maximum);
        await SendRequestLockedAsync(["seek", target / 1_000d, "absolute+exact"]).ConfigureAwait(false);
        Interlocked.Exchange(ref _positionMs, target);
    }

    private async Task<double?> ReadNumberPropertyLockedAsync(string property)
    {
        var data = await SendRequestLockedAsync(["get_property", property], throwOnError: false).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.Number } ? data.Value.GetDouble() : null;
    }

    private async Task<bool?> ReadBooleanPropertyLockedAsync(string property)
    {
        var data = await SendRequestLockedAsync(["get_property", property], throwOnError: false).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.True or JsonValueKind.False } ? data.Value.GetBoolean() : null;
    }

    private async Task<JsonElement?> SendRequestLockedAsync(object[] command, bool throwOnError = true)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.Serialize(new { command, request_id = requestId });
        await _writer.WriteLineAsync(request).WaitAsync(IpcTimeout).ConfigureAwait(false);

        while (true)
        {
            var line = await _reader.ReadLineAsync().WaitAsync(IpcTimeout).ConfigureAwait(false);
            if (line is null) throw new IOException("mpv IPC pipe closed.");
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (!root.TryGetProperty("request_id", out var responseId) || responseId.GetInt32() != requestId) continue;
            if (!root.TryGetProperty("error", out var error) || error.GetString() != "success")
            {
                if (throwOnError) throw new InvalidOperationException($"mpv 命令失败: {error.GetString()}");
                return null;
            }
            return root.TryGetProperty("data", out var data) ? data.Clone() : null;
        }
    }

    private async Task CleanupProcessAndPipeAsync()
    {
        await _ipcLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            _process.WaitForExit();
            DisposeExpected(_writer);
            DisposeExpected(_reader);
            DisposeExpected(_pipe);
            _process.Dispose();
        }
        finally
        {
            _ipcLock.Release();
        }

        while (Volatile.Read(ref _activeCommands) > 0)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        if (_transportLease is not null) await _transportLease.DisposeAsync().ConfigureAwait(false);
        _ipcLock.Dispose();
    }

    private void SaveCurrent(bool completed) =>
        _progressStore.Save(_episode.ProgressKey, PositionMs, DurationMs, completed);

    private static long ToMilliseconds(double seconds) =>
        Convert.ToInt64(Math.Max(0, seconds) * 1_000, CultureInfo.InvariantCulture);

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data)) AddError(e.Data);
    }

    private void AddError(string message)
    {
        lock (_errorSync)
        {
            if (_errors.Count == 8) _errors.Dequeue();
            _errors.Enqueue(message);
        }
    }

    private static void DisposeExpected(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // mpv owns the other end of IPC and can close it at any time.
        }
    }
}
