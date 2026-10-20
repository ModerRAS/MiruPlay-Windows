using System.Diagnostics;
using System.Globalization;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

internal sealed class LibMpvPlaybackSession : IPlaybackSession
{
    private static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(15);
    private const int MpvEventShutdown = 1;
    private const int MpvEventLogMessage = 2;
    private const int MpvEventEndFile = 7;
    private const int MpvEventFileLoaded = 8;
    private const int NativeLogLimit = 80;
    private const uint MpvFormatNode = 6;
    private static readonly string[] DiagnosticPropertyNames =
    [
        "vo",
        "ao",
        "hwdec-current",
        "video-codec",
        "audio-codec-name",
        "video-params/pixelformat",
        "video-params/hw-pixelformat",
        "video-params/primaries",
        "video-params/gamma",
        "video-params/sig-peak",
        "container-fps",
        "estimated-vf-fps",
        "display-sync-active",
        "decoder-frame-drop-count",
        "frame-drop-count",
        "cache-speed",
        "cache-buffering-state",
    ];
    private readonly ILibMpvClient _client;
    private readonly LibraryEpisode _episode;
    private readonly IPlaybackProgressStore _progressStore;
    private IAsyncDisposable? _transportLease;
    private readonly TaskCompletionSource<object?> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateSync = new();
    private readonly object _disposeSync = new();
    private readonly object _nativeLogSync = new();
    private readonly Queue<MpvNativeLogMessage> _nativeLogs = new();
    private readonly bool _surfaceAttached;
    private IReadOnlyList<MpvAudioTrack> _audioTracks = [];
    private IReadOnlyList<PlaybackSubtitleTrack> _subtitleTracks = [];
    private int? _selectedAudioTrackId;
    private int? _selectedSubtitleTrackId;
    private long _positionMs;
    private long _durationMs;
    private double _speed = 1;
    private double _volumePercent = 100;
    private bool _paused;
    private bool _muted;
    private string? _fileName;
    private string? _mediaTitle;
    private string? _containerFormat;
    private string? _audioCodec;
    private MpvVideoTrackInfo? _video;
    private AudioDspFilterGraph? _appliedAudioDsp;
    private AudioDspChannelLayout? _appliedAudioDspChannelLayout;
    private Task? _monitorTask;
    private int _ending;
    private int _explicitStop;
    private int _naturalEnd;
    private int _disposed;
    private int _completed;
    private long _lastPersistedPositionMs = -1;
    private long _lastPersistedDurationMs = -1;
    private Task? _disposeTask;

    internal LibMpvPlaybackSession(
        ILibMpvClient client,
        LibraryEpisode episode,
        IPlaybackProgressStore progressStore,
        IAsyncDisposable? transportLease,
        bool surfaceAttached = false)
    {
        _client = client;
        _episode = episode;
        _progressStore = progressStore;
        _transportLease = transportLease;
        _surfaceAttached = surfaceAttached;
        _durationMs = Convert.ToInt64(episode.Duration.TotalMilliseconds, CultureInfo.InvariantCulture);
    }

    internal static async Task<LibMpvPlaybackSession> StartAsync(
        ILibMpvClient client,
        LibraryEpisode episode,
        AppSettings settings,
        IPlaybackProgressStore progressStore,
        bool headless,
        IntPtr? windowHandle,
        IAsyncDisposable? transportLease,
        MpvWindowsVideoOptions? videoOptions,
        long? startPositionMs = null,
        long? endPositionMs = null)
    {
        var session = new LibMpvPlaybackSession(
            client,
            episode,
            progressStore,
            null,
            surfaceAttached: !headless && windowHandle is { } handle && handle != IntPtr.Zero);
        try
        {
            session.Configure(settings, headless, windowHandle, videoOptions, startPositionMs, endPositionMs);
            client.RequestLogMessages("warn");
            EnsureNativeSuccess(client.Initialize(), AppStrings.Get("mpv.initFailed"));
            EnsureNativeSuccess(client.Command(["loadfile", episode.MediaPath, "replace"]), AppStrings.Get("mpv.loadFailed"));
            await session.WaitForFileLoadedAsync().ConfigureAwait(false);
            session.LoadExternalSubtitles(episode.SubtitlePaths, settings.PreferredSubtitleLanguage);
            session.LoadExternalAudio(episode.ExternalAudioPaths);
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
        IPlaybackProgressStore progressStore,
        bool headless,
        IntPtr? windowHandle,
        IAsyncDisposable? transportLease,
        MpvWindowsVideoOptions? videoOptions,
        long? startPositionMs = null,
        long? endPositionMs = null)
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
                videoOptions,
                startPositionMs,
                endPositionMs);
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
    public double VolumePercent => Volatile.Read(ref _volumePercent);
    public bool IsMuted => Volatile.Read(ref _muted);
    public MpvPlaybackInfo PlaybackInfo => BuildPlaybackInfo();
    public AudioDspFilterGraph? AppliedAudioDsp => _appliedAudioDsp;
    public AudioDspChannelLayout? AppliedAudioDspChannelLayout => _appliedAudioDspChannelLayout;
    public bool IsActive => !Completion.IsCompleted && Volatile.Read(ref _ending) == 0;
    public bool IsPlaying => IsActive && !IsPaused;
    public MpvNativeDiagnostics GetNativeDiagnostics()
    {
        var properties = new List<MpvNativePropertySample>(DiagnosticPropertyNames.Length);
        if (Volatile.Read(ref _disposed) == 0)
        {
            foreach (var name in DiagnosticPropertyNames)
            {
                try
                {
                    var value = _client.GetPropertyString(name);
                    if (!string.IsNullOrWhiteSpace(value))
                        properties.Add(new(name, value.Length <= 256 ? value : value[..256]));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        MpvNativeLogMessage[] logs;
        lock (_nativeLogSync) logs = _nativeLogs.ToArray();
        return new(Environment.TickCount64, _surfaceAttached, properties, logs);
    }
    public event EventHandler? SubtitleTracksChanged;
    public event EventHandler? AudioTracksChanged;
    public event EventHandler? PlaybackInfoChanged;

    internal void StartMonitoring() => _monitorTask ??= MonitorAsync();

    public Task SetSubtitleTrackAsync(int? trackId) => ExecuteCommandAsync(new MpvPlaybackCommand("subtitle", SubtitleTrackId: trackId));

    public Task SetAudioTrackAsync(int? trackId) => ExecuteCommandAsync(new MpvPlaybackCommand("audio", AudioTrackId: trackId));

    public Task SeekAsync(long positionMs) => ExecuteCommandAsync(new MpvPlaybackCommand("seek", PositionMs: positionMs));

    public Task SeekRelativeAsync(long deltaMs) => ExecuteCommandAsync(new MpvPlaybackCommand("seek_relative", DeltaMs: deltaMs));

    public Task SetSpeedAsync(float speed) => ExecuteCommandAsync(new MpvPlaybackCommand("speed", Speed: speed));

    public Task SetVolumeAsync(double volumePercent) =>
        ExecuteCommandAsync(new MpvPlaybackCommand("set_volume", VolumePercent: volumePercent));

    public Task SetMutedAsync(bool muted) =>
        ExecuteCommandAsync(new MpvPlaybackCommand("set_mute", IsMuted: muted));

    public Task TogglePauseAsync() => ExecuteCommandAsync(new MpvPlaybackCommand("toggle"));

    public Task ApplyAudioDspAsync(AudioDspFilterGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        EnsureActive();
        var value = string.IsNullOrWhiteSpace(graph.AfValue) ? string.Empty : $"lavfi=[{graph.AfValue}]";
        EnsureNativeSuccess(_client.SetPropertyString("af", value), AppStrings.Get("mpv.afFailed"));
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
            request.AudioTrackId,
            request.VolumePercent,
            request.IsMuted));
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
                EnsureNativeSuccess(_client.Command(["cycle", "pause"]), AppStrings.Get("mpv.toggleFailed"));
                _paused = !_paused;
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "cycle_mute":
                EnsureNativeSuccess(_client.Command(["cycle", "mute"]), "libmpv mute toggle failed");
                _muted = TryReadBoolean("mute") ?? !_muted;
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "volume":
                var volumeDelta = request.DeltaMs ?? 0;
                EnsureNativeSuccess(
                    _client.Command(["add", "volume", volumeDelta.ToString(CultureInfo.InvariantCulture)]),
                    "libmpv volume adjustment failed");
                Volatile.Write(ref _volumePercent, Math.Clamp(
                    TryReadDouble("volume") ?? VolumePercent + volumeDelta,
                    0,
                    100));
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "set_volume":
                var volumePercent = request.VolumePercent ?? throw new ArgumentException(AppStrings.Get("mpv.noVolume"), nameof(request));
                if (!double.IsFinite(volumePercent)) throw new ArgumentOutOfRangeException(nameof(request));
                volumePercent = Math.Clamp(volumePercent, 0, 100);
                SetProperty("volume", volumePercent.ToString("0.###", CultureInfo.InvariantCulture));
                Volatile.Write(ref _volumePercent, volumePercent);
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "set_mute":
                var muted = request.IsMuted ?? throw new ArgumentException(AppStrings.Get("mpv.noMute"), nameof(request));
                SetProperty("mute", muted ? "yes" : "no");
                _muted = muted;
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
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
                throw new ArgumentException(AppStrings.Get("mpv.unknownCommand", request.Command), nameof(request));
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);

        try
        {
            if (IsActive)
            {
                try
                {
                    await ExecuteCommandAsync(new MpvPlaybackCommand("stop")).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    RecordError(error);
                    Complete(false);
                }
            }

            if (_monitorTask is not null)
            {
                try
                {
                    await Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Complete(false);
                }
            }
            else
            {
                Complete(false);
            }
        }
        finally
        {
            try
            {
                _client.Dispose();
            }
            catch (Exception error)
            {
                RecordError(error);
            }

            var transportLease = Interlocked.Exchange(ref _transportLease, null);
            if (transportLease is not null)
            {
                try
                {
                    await transportLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    RecordError(error);
                }
            }
        }
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
                if (nativeEvent.EventId == MpvEventLogMessage && nativeEvent.LogMessage is { } loadLog)
                {
                    CaptureNativeLog(loadLog);
                    continue;
                }
                if (nativeEvent.EventId == MpvEventShutdown)
                    throw new InvalidOperationException(AppStrings.Get("mpv.closedEarly"));
                if (nativeEvent.EventId == MpvEventEndFile)
                {
                    var error = nativeEvent.EndFile?.Error ?? nativeEvent.Error;
                    throw new InvalidOperationException($"libmpv media load ended before file-loaded (error code {error}).");
                }
            }
            throw new TimeoutException(AppStrings.Get("mpv.loadTimeout"));
        }).ConfigureAwait(false);
    }

    private void MonitorLoop()
    {
        var sampleInterval = Stopwatch.StartNew();
        var progressInterval = Stopwatch.StartNew();
        try
        {
            while (IsActive)
            {
                var nativeEvent = _client.WaitEvent(0.2);
                if (nativeEvent.EventId == MpvEventShutdown) break;
                if (nativeEvent.EventId == MpvEventLogMessage && nativeEvent.LogMessage is { } logMessage)
                    CaptureNativeLog(logMessage);
                if (nativeEvent.EventId == MpvEventEndFile)
                {
                    HandleEndFile(nativeEvent);
                    break;
                }
                if (sampleInterval.Elapsed >= TimeSpan.FromMilliseconds(500))
                {
                    SampleRuntimeState();
                    sampleInterval.Restart();
                }
                if (progressInterval.Elapsed >= TimeSpan.FromSeconds(15))
                {
                    PersistProgressIfDue(progressInterval.Elapsed);
                    progressInterval.Restart();
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            RecordError(error);
        }
        finally
        {
            Complete(Volatile.Read(ref _explicitStop) == 0 && Volatile.Read(ref _naturalEnd) != 0);
        }
    }

    private void CaptureNativeLog(LibMpvLogMessage message)
    {
        var log = new MpvNativeLogMessage(
            Environment.TickCount64,
            message.Prefix,
            message.Level,
            RotatingLocalLogStore.Redact(message.Text));
        lock (_nativeLogSync)
        {
            if (_nativeLogs.Count == NativeLogLimit) _nativeLogs.Dequeue();
            _nativeLogs.Enqueue(log);
        }
    }

    private void Configure(
        AppSettings settings,
        bool headless,
        IntPtr? windowHandle,
        MpvWindowsVideoOptions? videoOptions,
        long? startPositionMs,
        long? endPositionMs)
    {
        SetOption("idle", "yes");
        SetOption("force-window", headless ? "no" : "yes");
        SetOption("keep-open", "yes");
        SetOption("resume-playback", "no");
        SetOption("sub-ass-override", "no");
        SetOption("audio-file-auto", _episode.ExternalAudioPaths.Count == 0 ? "fuzzy" : "no");
        SetOption("sub-auto", "fuzzy");
        if (headless)
        {
            SetOption("vo", "null");
            SetOption("ao", "null");
        }
        else
        {
            foreach (var argument in MpvWindowsVideoOptionMapper.BuildArguments(videoOptions)) SetOptionArgument(argument);
            SetOption("osc", "no");
            SetOption("input-default-bindings", "no");
            SetOption("input-vo-keyboard", "no");
            if (windowHandle is { } handle && handle != IntPtr.Zero)
                SetOption("wid", handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        }

        if (startPositionMs is > 0)
        {
            SetOption("start", (startPositionMs.Value / 1_000d).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var progress = _progressStore.Get(_episode.ProgressKey);
            if (progress is { IsCompleted: false, PositionMs: > 0 })
                SetOption("start", (progress.PositionMs / 1_000d).ToString(CultureInfo.InvariantCulture));
        }

        if (endPositionMs is > 0)
            SetOption("end", (endPositionMs.Value / 1_000d).ToString(CultureInfo.InvariantCulture));

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

        ApplyAudioSrcBypass(settings);

        if (settings.AudioDsp?.Enabled == true)
        {
            var audioDsp = settings.AudioDsp.Normalize();
            var preset = audioDsp.Presets!.First(item => item.Id.Equals(audioDsp.SelectedPresetId, StringComparison.OrdinalIgnoreCase));
            var layout = AudioDspChannelLayout.ForId(preset.ChannelLayoutId);
            var graph = AudioDspFilterGraphCompiler.Compile(audioDsp, layout, 48_000);
            foreach (var argument in graph.MpvArguments) SetOptionArgument(argument);
            _appliedAudioDsp = graph;
            _appliedAudioDspChannelLayout = layout;
        }
    }

    /// <summary>Sample-rate-converter output policy: system lets the device decide,
    /// software resamples to a fixed rate, direct requests exclusive WASAPI.
    /// Direct falls back to software mapping while DSP is enabled.</summary>
    private void ApplyAudioSrcBypass(AppSettings settings)
    {
        var dspEnabled = settings.AudioDsp?.Enabled == true;
        var mode = AppSettingsStore.NormalizeAudioSrcBypass(settings.AudioSrcBypass);
        if (mode == "direct" && !dspEnabled)
        {
            SetOption("ao", "wasapi");
            SetOption("ao-wasapi-exclusive", "yes");
            return;
        }
        if (mode == "software" || (mode == "direct" && dspEnabled))
            SetOption("audio-samplerate", AppSettingsStore.NormalizeAudioOutputSampleRate(settings.AudioOutputSampleRate)
                .ToString(CultureInfo.InvariantCulture));
    }

    private void LoadExternalAudio(IEnumerable<string> audioPaths)
    {
        foreach (var path in audioPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) && !IsRemotePath(path)) continue;
            var result = _client.Command(["audio-add", path, "auto"]);
            if (result < 0) LastError = AppStrings.Get("mpv.sidecarFailed", Path.GetFileName(path));
        }
    }

    private void LoadExternalSubtitles(IEnumerable<string> subtitlePaths, string preference)
    {
        var paths = subtitlePaths.Where(path => File.Exists(path) || IsRemotePath(path)).ToArray();
        if (paths.Length == 0) return;
        var ordered = MpvPlayerLauncher.PrioritizeSubtitlePaths(paths, preference);
        foreach (var path in ordered) EnsureNativeSuccess(_client.Command(["sub-add", path, "auto"]), AppStrings.Get("mpv.subAddFailed"));
        var preferredTrackId = FindExternalSubtitleTrackId(ordered[0]);
        if (preferredTrackId is not null)
            SetProperty("sid", preferredTrackId.Value.ToString(CultureInfo.InvariantCulture));
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
        EnsureNativeSuccess(_client.SetOptionString(name, value), AppStrings.Get("mpv.optionFailed", name));

    private static bool IsRemotePath(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void SampleRuntimeState()
    {
        if (!IsActive) return;
        if (TryReadDouble("time-pos") is { } position) Interlocked.Exchange(ref _positionMs, ToMilliseconds(position));
        if (TryReadDouble("duration") is { } duration) Interlocked.Exchange(ref _durationMs, ToMilliseconds(duration));
        if (TryReadBoolean("pause") is { } paused) _paused = paused;
        if (TryReadDouble("speed") is { } speed && double.IsFinite(speed)) Volatile.Write(ref _speed, speed);
        if (TryReadDouble("volume") is { } volume && double.IsFinite(volume))
            Volatile.Write(ref _volumePercent, Math.Clamp(volume, 0, 100));
        if (TryReadBoolean("mute") is { } muted) _muted = muted;
        _fileName = _client.GetPropertyString("filename");
        _mediaTitle = _client.GetPropertyString("media-title");
        _containerFormat = _client.GetPropertyString("file-format");
        _audioCodec = _client.GetPropertyString("audio-codec-name");
        var videoCodec = _client.GetPropertyString("video-codec");
        var pixelFormat = _client.GetPropertyString("video-params/pixelformat");
        var primaries = _client.GetPropertyString("video-params/primaries");
        var transfer = _client.GetPropertyString("video-params/gamma");
        var width = TryReadInt("video-params/w");
        var height = TryReadInt("video-params/h");
        _video = videoCodec is null && pixelFormat is null && primaries is null && transfer is null && width is null && height is null
            ? null
            : new(videoCodec, pixelFormat, primaries, transfer, width, height);
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

        var subtitleChanged = false;
        var audioChanged = false;
        lock (_stateSync)
        {
            var selectedSubtitleTrackId = ParseTrackId(_client.GetPropertyString("sid"));
            var selectedAudioTrackId = ParseTrackId(_client.GetPropertyString("aid"));
            subtitleChanged = !_subtitleTracks.SequenceEqual(subtitles) || _selectedSubtitleTrackId != selectedSubtitleTrackId;
            audioChanged = !_audioTracks.SequenceEqual(audio) || _selectedAudioTrackId != selectedAudioTrackId;
            _subtitleTracks = subtitles;
            _audioTracks = audio;
            _selectedSubtitleTrackId = selectedSubtitleTrackId;
            _selectedAudioTrackId = selectedAudioTrackId;
        }
        if (subtitleChanged) SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
        if (audioChanged) AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SeekAbsolute(long positionMs)
    {
        var seconds = Math.Max(0, positionMs) / 1_000d;
        EnsureNativeSuccess(_client.Command([
            "seek",
            seconds.ToString(CultureInfo.InvariantCulture),
            "absolute+exact",
        ]), AppStrings.Get("mpv.seekFailed"));
        Interlocked.Exchange(ref _positionMs, Math.Max(0, positionMs));
    }

    private void SetProperty(string name, string value) =>
        EnsureNativeSuccess(_client.SetPropertyString(name, value), AppStrings.Get("mpv.propFailed", name));

    private void UpdateSelectedSubtitle(int? trackId)
    {
        var changed = false;
        lock (_stateSync)
        {
            changed = _selectedSubtitleTrackId != trackId;
            _selectedSubtitleTrackId = trackId;
        }
        if (changed) SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectedAudio(int? trackId)
    {
        var changed = false;
        lock (_stateSync)
        {
            changed = _selectedAudioTrackId != trackId;
            _selectedAudioTrackId = trackId;
        }
        if (changed) AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Complete(bool completed)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Interlocked.Exchange(ref _ending, 1);
        WasCompleted = completed;
        try
        {
            TrySaveCurrent(completed);
        }
        finally
        {
            _completionSource.TrySetResult(null);
        }
    }

    internal bool PersistProgressIfDue(TimeSpan elapsed)
    {
        if (elapsed < ProgressSaveInterval) return false;
        return TrySaveCurrent(completed: false);
    }

    private bool TrySaveCurrent(bool completed)
    {
        var positionMs = PositionMs;
        var durationMs = DurationMs;
        if (!completed && (durationMs <= 0 || positionMs == _lastPersistedPositionMs && durationMs == _lastPersistedDurationMs)) return false;
        try
        {
            _progressStore.Save(_episode.ProgressKey, positionMs, durationMs, completed);
            _lastPersistedPositionMs = positionMs;
            _lastPersistedDurationMs = durationMs;
            return true;
        }
        catch (Exception error)
        {
            LastError = AppStrings.Get("mpv.progressFailed", error.Message);
            return false;
        }
    }

    private void HandleEndFile(LibMpvEvent nativeEvent)
    {
        var endFile = nativeEvent.EndFile;
        if (endFile?.Reason == LibMpvEndFileReason.Eof)
        {
            Interlocked.Exchange(ref _naturalEnd, 1);
            return;
        }

        var error = endFile?.Error ?? nativeEvent.Error;
        var reason = endFile?.Reason.ToString() ?? "unknown";
        if (error < 0 || endFile?.Reason == LibMpvEndFileReason.Error)
            LastError = $"libmpv playback ended with {reason} (error code {error}).";
    }

    private int? FindExternalSubtitleTrackId(string path)
    {
        var node = _client.GetPropertyNode("track-list", MpvFormatNode);
        var values = node?.ArrayValue ?? node?.MapValue?.Values ?? [];
        foreach (var item in values)
        {
            if (GetString(item, "type") != "sub") continue;
            var externalPath = GetString(item, "external-filename");
            if (externalPath is not null && string.Equals(externalPath, path, StringComparison.OrdinalIgnoreCase))
                return GetInt(item, "id");
        }
        return null;
    }

    private void RecordError(Exception error) => LastError = $"libmpv: {error.Message}";

    private void EnsureActive()
    {
        if (!IsActive) throw new InvalidOperationException(AppStrings.Get("mpv.noSession"));
    }

    private static void EnsureNativeSuccess(int error, string message)
    {
        if (error < 0) throw new InvalidOperationException(AppStrings.Get("mpv.endFile", message, error));
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

    private int? TryReadInt(string property) =>
        int.TryParse(_client.GetPropertyString(property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

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
                _selectedSubtitleTrackId,
                VolumePercent,
                IsMuted);
        }
    }
}
