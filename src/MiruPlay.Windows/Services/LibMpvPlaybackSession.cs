using System.Diagnostics;
using System.Globalization;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

internal sealed class LibMpvPlaybackSession : IPlaybackSession
{
    private const int MpvEventShutdown = 1;
    private const int MpvEventEndFile = 7;
    private const int MpvEventFileLoaded = 8;
    private const uint MpvFormatNode = 6;
    private readonly ILibMpvClient _client;
    private readonly LibraryEpisode _episode;
    private readonly PlaybackProgressStore _progressStore;
    private IAsyncDisposable? _transportLease;
    private readonly TaskCompletionSource<object?> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateSync = new();
    private IReadOnlyList<MpvAudioTrack> _audioTracks = [];
    private IReadOnlyList<PlaybackSubtitleTrack> _subtitleTracks = [];
    private int? _selectedAudioTrackId;
    private int? _selectedSubtitleTrackId;
    private long _positionMs;
    private long _durationMs;
    private double _speed = 1;
    private bool _paused;
    private string? _fileName;
    private string? _mediaTitle;
    private string? _containerFormat;
    private string? _audioCodec;
    private MpvVideoTrackInfo? _video;
    private AudioDspFilterGraph? _appliedAudioDsp;
    private Task? _monitorTask;
    private int _ending;
    private int _explicitStop;
    private int _naturalEnd;
    private int _disposed;

    internal LibMpvPlaybackSession(
        ILibMpvClient client,
        LibraryEpisode episode,
        PlaybackProgressStore progressStore,
        IAsyncDisposable? transportLease)
    {
        _client = client;
        _episode = episode;
        _progressStore = progressStore;
        _transportLease = transportLease;
        _durationMs = Convert.ToInt64(episode.Duration.TotalMilliseconds, CultureInfo.InvariantCulture);
    }

    internal static async Task<LibMpvPlaybackSession> StartAsync(
        ILibMpvClient client,
        LibraryEpisode episode,
        AppSettings settings,
        PlaybackProgressStore progressStore,
        bool headless,
        IntPtr? windowHandle,
        IAsyncDisposable? transportLease,
        MpvWindowsVideoOptions? videoOptions)
    {
        var session = new LibMpvPlaybackSession(client, episode, progressStore, null);
        try
        {
            session.Configure(settings, headless, windowHandle, videoOptions);
            EnsureNativeSuccess(client.Initialize(), "初始化 libmpv 失败");
            EnsureNativeSuccess(client.Command(["loadfile", episode.MediaPath, "replace"]), "加载媒体到 libmpv 失败");
            await session.WaitForFileLoadedAsync().ConfigureAwait(false);
            session.LoadExternalSubtitles(episode.SubtitlePaths, settings.PreferredSubtitleLanguage);
            session._transportLease = transportLease;
            session.StartMonitoring();
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static Task<LibMpvPlaybackSession> StartAsync(
        string libraryPath,
        LibraryEpisode episode,
        AppSettings settings,
        PlaybackProgressStore progressStore,
        bool headless,
        IntPtr? windowHandle,
        IAsyncDisposable? transportLease,
        MpvWindowsVideoOptions? videoOptions)
    {
        var client = LibMpvClient.Create(libraryPath);
        try
        {
            return StartAsync(
                client,
                episode,
                settings,
                progressStore,
                headless,
                windowHandle,
                transportLease,
                videoOptions);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task Completion => _completionSource.Task;
    public LibraryEpisode Episode => _episode;
    public bool WasCompleted { get; private set; }
    public long PositionMs => Interlocked.Read(ref _positionMs);
    public long DurationMs => Interlocked.Read(ref _durationMs);
    public bool IsPaused => Volatile.Read(ref _paused);
    public string? LastError { get; private set; }
    public IReadOnlyList<PlaybackSubtitleTrack> SubtitleTracks
    {
        get { lock (_stateSync) return _subtitleTracks.ToArray(); }
    }
    public int? SelectedSubtitleTrackId
    {
        get { lock (_stateSync) return _selectedSubtitleTrackId; }
    }
    public IReadOnlyList<MpvAudioTrack> AudioTracks
    {
        get { lock (_stateSync) return _audioTracks.ToArray(); }
    }
    public int? SelectedAudioTrackId
    {
        get { lock (_stateSync) return _selectedAudioTrackId; }
    }
    public double Speed => Volatile.Read(ref _speed);
    public MpvPlaybackInfo PlaybackInfo => BuildPlaybackInfo();
    public AudioDspFilterGraph? AppliedAudioDsp => _appliedAudioDsp;
    public bool IsActive => !Completion.IsCompleted && Volatile.Read(ref _ending) == 0;
    public bool IsPlaying => IsActive && !IsPaused;
    public event EventHandler? SubtitleTracksChanged;
    public event EventHandler? AudioTracksChanged;
    public event EventHandler? PlaybackInfoChanged;

    internal void StartMonitoring() => _monitorTask ??= MonitorAsync();

    public Task SetSubtitleTrackAsync(int? trackId) => ExecuteCommandAsync(new MpvPlaybackCommand("subtitle", SubtitleTrackId: trackId));

    public Task SetAudioTrackAsync(int? trackId) => ExecuteCommandAsync(new MpvPlaybackCommand("audio", AudioTrackId: trackId));

    public Task SeekAsync(long positionMs) => ExecuteCommandAsync(new MpvPlaybackCommand("seek", PositionMs: positionMs));

    public Task SeekRelativeAsync(long deltaMs) => ExecuteCommandAsync(new MpvPlaybackCommand("seek_relative", DeltaMs: deltaMs));

    public Task SetSpeedAsync(float speed) => ExecuteCommandAsync(new MpvPlaybackCommand("speed", Speed: speed));

    public Task TogglePauseAsync() => ExecuteCommandAsync(new MpvPlaybackCommand("toggle"));

    public Task ApplyAudioDspAsync(AudioDspFilterGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        EnsureActive();
        var value = string.IsNullOrWhiteSpace(graph.AfValue) ? string.Empty : $"lavfi=[{graph.AfValue}]";
        EnsureNativeSuccess(_client.SetPropertyString("af", value), "设置 libmpv 音频滤镜失败");
        _appliedAudioDsp = graph;
        return Task.CompletedTask;
    }

    public Task<string?> GetAudioFilterGraphAsync()
    {
        if (!IsActive) return Task.FromResult<string?>(null);
        return Task.FromResult(_client.GetPropertyString("af"));
    }

    public Task<MpvPlaybackInfo> GetPlaybackInfoAsync()
    {
        if (IsActive) SampleRuntimeState();
        return Task.FromResult(PlaybackInfo);
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

    public Task ExecuteCommandAsync(MpvPlaybackCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureActive();
        switch (request.Command.Trim().ToLowerInvariant())
        {
            case "pause":
                SetProperty("pause", "yes");
                _paused = true;
                break;
            case "resume":
            case "play":
                SetProperty("pause", "no");
                _paused = false;
                break;
            case "toggle":
                EnsureNativeSuccess(_client.Command(["cycle", "pause"]), "切换 libmpv 暂停状态失败");
                _paused = !_paused;
                break;
            case "seek":
                SeekAbsolute(request.PositionMs ?? 0);
                break;
            case "seek_relative":
                SeekAbsolute(PositionMs + (request.DeltaMs ?? 0));
                break;
            case "skip_forward":
                SeekAbsolute(PositionMs + (request.DeltaMs ?? 30_000));
                break;
            case "skip_backward":
                SeekAbsolute(PositionMs - (request.DeltaMs ?? 10_000));
                break;
            case "speed":
                var speed = request.Speed ?? 1;
                if (!float.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(request));
                SetProperty("speed", speed.ToString(CultureInfo.InvariantCulture));
                Volatile.Write(ref _speed, speed);
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "subtitle":
                SetProperty("sid", request.SubtitleTrackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
                UpdateSelectedSubtitle(request.SubtitleTrackId);
                break;
            case "audio":
                SetProperty("aid", request.AudioTrackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
                UpdateSelectedAudio(request.AudioTrackId);
                break;
            case "stop":
                Interlocked.Exchange(ref _explicitStop, 1);
                Interlocked.Exchange(ref _ending, 1);
                _client.Command(["quit"]);
                if (_monitorTask is null) Complete(false);
                break;
            default:
                throw new ArgumentException($"未知播放命令: {request.Command}", nameof(request));
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && IsActive)
        {
            try
            {
                await ExecuteCommandAsync(new MpvPlaybackCommand("stop")).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Complete(false);
            }
        }

        if (_monitorTask is not null)
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        else
        {
            Complete(false);
        }
        _client.Dispose();
        if (_transportLease is not null) await _transportLease.DisposeAsync().ConfigureAwait(false);
    }

    private Task MonitorAsync() => Task.Run(MonitorLoop);

    private async Task WaitForFileLoadedAsync()
    {
        await Task.Run(() =>
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var nativeEvent = _client.WaitEvent(0.2);
                if (nativeEvent.EventId == MpvEventFileLoaded) return;
                if (nativeEvent.EventId == MpvEventShutdown)
                    throw new InvalidOperationException("libmpv 在媒体加载完成前关闭了会话。");
                if (nativeEvent.EventId == MpvEventEndFile && nativeEvent.Error < 0)
                    throw new InvalidOperationException($"libmpv 加载媒体失败（错误码 {nativeEvent.Error}）。");
            }
            throw new TimeoutException("libmpv 在 30 秒内没有完成媒体加载。");
        }).ConfigureAwait(false);
    }

    private void MonitorLoop()
    {
        var sampleInterval = Stopwatch.StartNew();
        try
        {
            while (IsActive)
            {
                var nativeEvent = _client.WaitEvent(0.2);
                if (nativeEvent.EventId == MpvEventShutdown) break;
                if (nativeEvent.EventId == MpvEventEndFile)
                {
                    Interlocked.Exchange(ref _naturalEnd, 1);
                    break;
                }
                if (sampleInterval.Elapsed >= TimeSpan.FromMilliseconds(500))
                {
                    SampleRuntimeState();
                    sampleInterval.Restart();
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            LastError = $"libmpv: {error.Message}";
        }
        finally
        {
            Complete(Volatile.Read(ref _explicitStop) == 0 &&
                (Volatile.Read(ref _naturalEnd) != 0 || DurationMs > 0 && PositionMs >= DurationMs * 0.9));
        }
    }

    private void Configure(
        AppSettings settings,
        bool headless,
        IntPtr? windowHandle,
        MpvWindowsVideoOptions? videoOptions)
    {
        SetOption("idle", "yes");
        SetOption("force-window", headless ? "no" : "yes");
        SetOption("keep-open", "yes");
        SetOption("resume-playback", "no");
        SetOption("sub-ass-override", "strip");
        if (headless)
        {
            SetOption("vo", "null");
            SetOption("ao", "null");
        }
        else
        {
            foreach (var argument in MpvWindowsVideoOptionMapper.BuildArguments(videoOptions)) SetOptionArgument(argument);
            SetOption("osc", "no");
            SetOption("input-default-bindings", "yes");
            if (windowHandle is { } handle && handle != IntPtr.Zero)
                SetOption("wid", handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        }

        var progress = _progressStore.Get(_episode.ProgressKey);
        if (progress is { IsCompleted: false, PositionMs: > 0 })
            SetOption("start", (progress.PositionMs / 1_000d).ToString(CultureInfo.InvariantCulture));

        var languages = settings.PreferredSubtitleLanguage switch
        {
            "zh_hans" => "zh-Hans,zh-CN,chs,sc,chi,zho",
            "zh_hant" => "zh-Hant,zh-TW,cht,tc,chi,zho",
            "zh" => "zh-Hans,zh-Hant,zh-CN,zh-TW,chi,zho",
            "en" => "eng,en",
            "ja" => "jpn,ja",
            _ => null,
        };
        if (languages is not null) SetOption("slang", languages);

        if (settings.AudioDsp?.Enabled == true)
        {
            var audioDsp = settings.AudioDsp.Normalize();
            var preset = audioDsp.Presets!.First(item => item.Id.Equals(audioDsp.SelectedPresetId, StringComparison.OrdinalIgnoreCase));
            var graph = AudioDspFilterGraphCompiler.Compile(
                audioDsp,
                AudioDspChannelLayout.ForId(preset.ChannelLayoutId),
                48_000);
            foreach (var argument in graph.MpvArguments) SetOptionArgument(argument);
            _appliedAudioDsp = graph;
        }
    }

    private void LoadExternalSubtitles(IEnumerable<string> subtitlePaths, string preference)
    {
        var paths = subtitlePaths.Where(path => File.Exists(path) || IsRemotePath(path)).ToArray();
        if (paths.Length == 0) return;
        var ordered = MpvPlayerLauncher.PrioritizeSubtitlePaths(paths, preference);
        foreach (var path in ordered) EnsureNativeSuccess(_client.Command(["sub-add", path, "select"]), "加载外挂字幕到 libmpv 失败");
    }

    private void SetOptionArgument(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal)) return;
        var option = argument[2..];
        var separator = option.IndexOf('=');
        SetOption(
            separator < 0 ? option : option[..separator],
            separator < 0 ? "yes" : option[(separator + 1)..]);
    }

    private void SetOption(string name, string value) =>
        EnsureNativeSuccess(_client.SetOptionString(name, value), $"设置 libmpv 选项 {name} 失败");

    private static bool IsRemotePath(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void SampleRuntimeState()
    {
        if (!IsActive) return;
        if (TryReadDouble("time-pos") is { } position) Interlocked.Exchange(ref _positionMs, ToMilliseconds(position));
        if (TryReadDouble("duration") is { } duration) Interlocked.Exchange(ref _durationMs, ToMilliseconds(duration));
        if (TryReadBoolean("pause") is { } paused) _paused = paused;
        if (TryReadDouble("speed") is { } speed && double.IsFinite(speed)) Volatile.Write(ref _speed, speed);
        _fileName = _client.GetPropertyString("filename");
        _mediaTitle = _client.GetPropertyString("media-title");
        _containerFormat = _client.GetPropertyString("file-format");
        _audioCodec = _client.GetPropertyString("audio-codec-name");
        var videoCodec = _client.GetPropertyString("video-codec");
        _video = videoCodec is null ? null : new(videoCodec, null, null, null, null, null);
        UpdateTracks();
        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
        if (TryReadBoolean("eof-reached") == true)
        {
            Interlocked.Exchange(ref _naturalEnd, 1);
            Interlocked.Exchange(ref _ending, 1);
        }
    }

    private void UpdateTracks()
    {
        var node = _client.GetPropertyNode("track-list", MpvFormatNode);
        if (node?.Kind is not (LibMpvNodeKind.Array or LibMpvNodeKind.Map)) return;
        var values = node.ArrayValue ?? node.MapValue?.Values ?? [];
        var subtitles = new List<PlaybackSubtitleTrack>();
        var audio = new List<MpvAudioTrack>();
        foreach (var item in values)
        {
            if (item.MapValue is null || GetInt(item, "id") is not { } id) continue;
            var type = GetString(item, "type");
            var language = GetString(item, "lang") ?? "und";
            var title = GetString(item, "title") ?? string.Empty;
            var codec = GetString(item, "codec") ?? string.Empty;
            var externalFileName = GetString(item, "external-filename");
            var external = GetBool(item, "external") || !string.IsNullOrWhiteSpace(externalFileName);
            var selected = GetBool(item, "selected");
            if (type == "sub") subtitles.Add(new(id, language, title, codec, external, externalFileName, selected));
            if (type == "audio") audio.Add(new(id, language, title, codec, external, selected));
        }

        lock (_stateSync)
        {
            _subtitleTracks = subtitles;
            _audioTracks = audio;
            _selectedSubtitleTrackId = ParseTrackId(_client.GetPropertyString("sid"));
            _selectedAudioTrackId = ParseTrackId(_client.GetPropertyString("aid"));
        }
        SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SeekAbsolute(long positionMs)
    {
        var seconds = Math.Max(0, positionMs) / 1_000d;
        EnsureNativeSuccess(_client.Command([
            "seek",
            seconds.ToString(CultureInfo.InvariantCulture),
            "absolute+exact",
        ]), "跳转 libmpv 播放位置失败");
        Interlocked.Exchange(ref _positionMs, Math.Max(0, positionMs));
    }

    private void SetProperty(string name, string value) =>
        EnsureNativeSuccess(_client.SetPropertyString(name, value), $"设置 libmpv 属性 {name} 失败");

    private void UpdateSelectedSubtitle(int? trackId)
    {
        lock (_stateSync) _selectedSubtitleTrackId = trackId;
        SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectedAudio(int? trackId)
    {
        lock (_stateSync) _selectedAudioTrackId = trackId;
        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Complete(bool completed)
    {
        if (_completionSource.Task.IsCompleted) return;
        Interlocked.Exchange(ref _ending, 1);
        WasCompleted = completed;
        SaveCurrent(completed);
        _completionSource.TrySetResult(null);
    }

    private void SaveCurrent(bool completed) =>
        _progressStore.Save(_episode.ProgressKey, PositionMs, DurationMs, completed);

    private void EnsureActive()
    {
        if (!IsActive) throw new InvalidOperationException("当前没有可控制的 libmpv 播放会话。");
    }

    private static void EnsureNativeSuccess(int error, string message)
    {
        if (error < 0) throw new InvalidOperationException($"{message}（错误码 {error}）。");
    }

    private double? TryReadDouble(string property)
    {
        var value = _client.GetPropertyString(property);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result
            : null;
    }

    private bool? TryReadBoolean(string property)
    {
        var value = _client.GetPropertyString(property);
        return value?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null,
        };
    }

    private static string? GetString(LibMpvNodeValue item, string key) =>
        item.MapValue?.GetValueOrDefault(key)?.StringValue;

    private static int? GetInt(LibMpvNodeValue item, string key)
    {
        var value = item.MapValue?.GetValueOrDefault(key);
        return value?.Int64Value is { } result && result is >= int.MinValue and <= int.MaxValue ? (int)result : null;
    }

    private static bool GetBool(LibMpvNodeValue item, string key) => item.MapValue?.GetValueOrDefault(key)?.FlagValue == true;

    private static int? ParseTrackId(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static long ToMilliseconds(double seconds) =>
        double.IsFinite(seconds) ? Convert.ToInt64(Math.Max(0, seconds) * 1_000, CultureInfo.InvariantCulture) : 0;

    private MpvPlaybackInfo BuildPlaybackInfo()
    {
        lock (_stateSync)
        {
            return new(
                PositionMs,
                DurationMs,
                IsPaused,
                Speed,
                _fileName,
                _mediaTitle,
                _containerFormat,
                _audioCodec,
                _video,
                _audioTracks.ToArray(),
                _subtitleTracks.ToArray(),
                _selectedAudioTrackId,
                _selectedSubtitleTrackId);
        }
    }
}
