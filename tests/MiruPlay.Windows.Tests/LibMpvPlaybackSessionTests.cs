using MiruPlay.Windows.Services;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Tests;

public sealed class LibMpvPlaybackSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-libmpv-session-{Guid.NewGuid():N}");

    public LibMpvPlaybackSessionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void EndFileReasonValuesMatchTheLibMpvAbi()
    {
        Assert.Equal(0, (int)LibMpvEndFileReason.Eof);
        Assert.Equal(2, (int)LibMpvEndFileReason.Stop);
        Assert.Equal(3, (int)LibMpvEndFileReason.Quit);
        Assert.Equal(4, (int)LibMpvEndFileReason.Error);
        Assert.Equal(5, (int)LibMpvEndFileReason.Redirect);
    }

    [Fact]
    public async Task ApplyingAudioDspUsesTheExistingLavfiPropertyContract()
    {
        var client = new FakeLibMpvClient();
        var episode = new LibraryEpisode(
            1,
            "episode-uuid",
            "episode-progress",
            1,
            1,
            1,
            "Episode",
            Path.Combine(_directory, "episode.mkv"),
            TimeSpan.FromMinutes(24),
            []);
        var graph = new AudioDspFilterGraph("volume=3dB", [], "stereo", []);
        await using var session = new LibMpvPlaybackSession(
            client,
            episode,
            new PlaybackProgressStore(Path.Combine(_directory, "state.db")),
            null);

        await session.ApplyAudioDspAsync(graph);

        Assert.Equal("lavfi=[volume=3dB]", client.Properties["af"]);
        Assert.Same(graph, session.AppliedAudioDsp);
    }

    [Fact]
    public async Task KeyboardAudioCommandsMapToMpvMuteAndVolumeCommands()
    {
        var client = new FakeLibMpvClient();
        var episode = new LibraryEpisode(
            1,
            "episode-uuid",
            "episode-progress",
            1,
            1,
            1,
            "Episode",
            Path.Combine(_directory, "episode.mkv"),
            TimeSpan.FromMinutes(24),
            []);
        await using var session = new LibMpvPlaybackSession(
            client,
            episode,
            new PlaybackProgressStore(Path.Combine(_directory, "keyboard-state.db")),
            null);

        await session.ExecuteCommandAsync(new MpvPlaybackCommand("cycle_mute"));
        await session.ExecuteCommandAsync(new MpvPlaybackCommand("volume", DeltaMs: 5));
        await session.ExecuteCommandAsync(new MpvPlaybackCommand("volume", DeltaMs: -5));

        Assert.Contains(client.Commands, command => command.SequenceEqual(["cycle", "mute"]));
        Assert.Contains(client.Commands, command => command.SequenceEqual(["add", "volume", "5"]));
        Assert.Contains(client.Commands, command => command.SequenceEqual(["add", "volume", "-5"]));
    }

    [Fact]
    public async Task AbsoluteVolumeAndMuteCommandsUpdateConfirmedPlaybackState()
    {
        var client = new FakeLibMpvClient();
        await using var session = new LibMpvPlaybackSession(
            client,
            CreateEpisode(Path.Combine(_directory, "volume.mkv")),
            new PlaybackProgressStore(Path.Combine(_directory, "volume-state.db")),
            null);

        await session.ExecuteCommandAsync(new PlaybackControlCommand("set_volume", VolumePercent: 47.5));
        await session.ExecuteCommandAsync(new PlaybackControlCommand("set_mute", IsMuted: true));

        Assert.Equal("47.5", client.Properties["volume"]);
        Assert.Equal("yes", client.Properties["mute"]);
        Assert.Equal(47.5, session.VolumePercent);
        Assert.True(session.IsMuted);
        Assert.Equal(47.5, session.PlaybackInfo.VolumePercent);
        Assert.True(session.PlaybackInfo.IsMuted);
    }

    [Fact]
    public async Task StartConfiguresEmbeddedD3d11WindowAndLoadsMedia()
    {
        var client = new FakeLibMpvClient();
        var mediaPath = Path.Combine(_directory, "episode.mkv");
        File.WriteAllBytes(mediaPath, []);
        var episode = new LibraryEpisode(1, "episode-uuid", "episode-progress", 1, 1, 1, "Episode", mediaPath, TimeSpan.FromMinutes(24), []);

        await using var session = await LibMpvPlaybackSession.StartAsync(
            client,
            episode,
            new AppSettings(),
            new PlaybackProgressStore(Path.Combine(_directory, "start-state.db")),
            headless: false,
            windowHandle: new IntPtr(1234),
            transportLease: null,
            videoOptions: new MpvWindowsVideoOptions());

        Assert.Equal("1234", client.Options["wid"]);
        Assert.Equal("gpu-next", client.Options["vo"]);
        Assert.Equal("d3d11", client.Options["gpu-api"]);
        Assert.Contains(client.Commands, command => command.SequenceEqual(["loadfile", mediaPath, "replace"]));
        Assert.Equal("no", client.Options["input-default-bindings"]);
    }

    [Fact]
    public async Task ErrorEndFileDoesNotCompletePlaybackAndExposesTheNativeFailure()
    {
        var client = new FakeLibMpvClient(
            new LibMpvEvent(8, 0, 0, IntPtr.Zero),
            new LibMpvEvent(7, 0, 0, IntPtr.Zero, new LibMpvEndFileEvent(LibMpvEndFileReason.Error, -12)));
        var mediaPath = Path.Combine(_directory, "error-end.mkv");
        File.WriteAllBytes(mediaPath, []);

        await using var session = await LibMpvPlaybackSession.StartAsync(
            client,
            CreateEpisode(mediaPath),
            new AppSettings(),
            new PlaybackProgressStore(Path.Combine(_directory, "error-end-state.db")),
            headless: true,
            windowHandle: null,
            transportLease: null,
            videoOptions: null);

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(session.WasCompleted);
        Assert.Contains("-12", session.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitStartPositionOverridesSavedResumePosition()
    {
        var mediaPath = Path.Combine(_directory, "resume.mkv");
        File.WriteAllBytes(mediaPath, []);
        var episode = CreateEpisode(mediaPath);
        var progressStore = new PlaybackProgressStore(Path.Combine(_directory, "resume-state.db"));
        progressStore.Save(episode.ProgressKey, 66_000, 1_440_000);
        var client = new FakeLibMpvClient(new LibMpvEvent(8, 0, 0, IntPtr.Zero));

        await using var session = await LibMpvPlaybackSession.StartAsync(
            client,
            episode,
            new AppSettings(),
            progressStore,
            headless: true,
            windowHandle: null,
            transportLease: null,
            videoOptions: null,
            startPositionMs: 12_345);

        Assert.Equal("12.345", client.Options["start"]);
    }

    [Fact]
    public async Task DisposalIsIdempotentAndReleasesTheNativeClientOnce()
    {
        var client = new FakeLibMpvClient();
        var session = new LibMpvPlaybackSession(
            client,
            CreateEpisode(Path.Combine(_directory, "dispose.mkv")),
            new PlaybackProgressStore(Path.Combine(_directory, "dispose-state.db")),
            null);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.True(session.Completion.IsCompleted);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task ProgressSaveFailureCannotBlockCompletionOrNativeAndTransportCleanup()
    {
        var client = new FakeLibMpvClient(
            new LibMpvEvent(8, 0, 0, IntPtr.Zero),
            new LibMpvEvent(7, 0, 0, IntPtr.Zero, new LibMpvEndFileEvent(LibMpvEndFileReason.Eof, 0)));
        var lease = new RecordingAsyncDisposable();
        var mediaPath = Path.Combine(_directory, "save-failure.mkv");
        File.WriteAllBytes(mediaPath, []);
        var session = await LibMpvPlaybackSession.StartAsync(
            client,
            CreateEpisode(mediaPath),
            new AppSettings(),
            new ThrowingProgressStore(),
            headless: true,
            windowHandle: null,
            transportLease: lease,
            videoOptions: null);

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();

        Assert.True(session.Completion.IsCompletedSuccessfully);
        Assert.True(session.WasCompleted);
        Assert.Contains("进度", session.LastError, StringComparison.Ordinal);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task PeriodicProgressSaveRunsAtFifteenSecondsAndSkipsUnchangedSamples()
    {
        var client = new FakeLibMpvClient();
        client.Properties["time-pos"] = "23.5";
        client.Properties["duration"] = "1440";
        var progress = new RecordingProgressStore();
        await using var session = new LibMpvPlaybackSession(
            client,
            CreateEpisode(Path.Combine(_directory, "periodic.mkv")),
            progress,
            null);
        await session.GetPlaybackInfoAsync();

        Assert.False(session.PersistProgressIfDue(TimeSpan.FromSeconds(14.999)));
        Assert.True(session.PersistProgressIfDue(TimeSpan.FromSeconds(15)));
        Assert.False(session.PersistProgressIfDue(TimeSpan.FromSeconds(30)));

        var save = Assert.Single(progress.Saves);
        Assert.Equal((23_500L, 1_440_000L, false), (save.PositionMs, save.DurationMs, save.Completed));
    }

    [Fact]
    public async Task ExternalSubtitlesAreAddedBeforeThePreferredTrackIsSelected()
    {
        var simplified = Path.Combine(_directory, "episode.zh-CN.srt");
        var english = Path.Combine(_directory, "episode.en.srt");
        File.WriteAllText(simplified, string.Empty);
        File.WriteAllText(english, string.Empty);
        var mediaPath = Path.Combine(_directory, "subtitles.mkv");
        File.WriteAllBytes(mediaPath, []);
        var client = new FakeLibMpvClient(new LibMpvEvent(8, 0, 0, IntPtr.Zero))
        {
            TrackList = Tracks(
                Track(41, "sub", externalFileName: english),
                Track(42, "sub", externalFileName: simplified)),
        };

        await using var session = await LibMpvPlaybackSession.StartAsync(
            client,
            CreateEpisode(mediaPath, [english, simplified]),
            new AppSettings { PreferredSubtitleLanguage = "zh_hans" },
            new PlaybackProgressStore(Path.Combine(_directory, "subtitle-state.db")),
            headless: true,
            windowHandle: null,
            transportLease: null,
            videoOptions: null);

        Assert.Contains(client.Commands, command => command.SequenceEqual(["sub-add", simplified, "auto"]));
        Assert.Contains(client.Commands, command => command.SequenceEqual(["sub-add", english, "auto"]));
        Assert.Equal("42", client.Properties["sid"]);
    }

    [Fact]
    public async Task RuntimeSamplingPopulatesVideoMetadataAndOnlyRaisesTrackEventsForChanges()
    {
        var client = new FakeLibMpvClient
        {
            TrackList = Tracks(Track(7, "audio", language: "jpn", codec: "aac", selected: true)),
        };
        client.Properties["video-codec"] = "hevc";
        client.Properties["video-params/pixelformat"] = "yuv420p10";
        client.Properties["video-params/primaries"] = "bt.2020";
        client.Properties["video-params/gamma"] = "pq";
        client.Properties["video-params/w"] = "3840";
        client.Properties["video-params/h"] = "2160";
        client.Properties["aid"] = "7";
        var session = new LibMpvPlaybackSession(
            client,
            CreateEpisode(Path.Combine(_directory, "metadata.mkv")),
            new PlaybackProgressStore(Path.Combine(_directory, "metadata-state.db")),
            null);
        var audioChanges = 0;
        session.AudioTracksChanged += (_, _) => audioChanges++;

        var first = await session.GetPlaybackInfoAsync();
        await session.GetPlaybackInfoAsync();

        Assert.Equal(new MpvVideoTrackInfo("hevc", "yuv420p10", "bt.2020", "pq", 3840, 2160), first.Video);
        Assert.True(first.Video!.IsHdr);
        Assert.Equal(1, audioChanges);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task NativeDiagnosticsCaptureAllowlistedPropertiesAndBoundedRedactedLogs()
    {
        var client = new FakeLibMpvClient(
            new LibMpvEvent(2, 0, 0, IntPtr.Zero, LogMessage: new LibMpvLogMessage("vo", 20, "load token=abc123")),
            new LibMpvEvent(8, 0, 0, IntPtr.Zero),
            new LibMpvEvent(2, 0, 0, IntPtr.Zero, LogMessage: new LibMpvLogMessage("ao", 20, "plain message")),
            new LibMpvEvent(1, 0, 0, IntPtr.Zero));
        client.Properties["vo"] = "gpu-next";
        client.Properties["audio-codec-name"] = "aac";
        client.Properties["cache-speed"] = string.Empty;
        client.Properties["unrelated-property"] = "no";
        client.Properties["video-params/primaries"] = new string('x', 300);
        var mediaPath = Path.Combine(_directory, "diagnostics.mkv");
        File.WriteAllBytes(mediaPath, []);

        await using var session = await LibMpvPlaybackSession.StartAsync(
            client,
            CreateEpisode(mediaPath),
            new AppSettings(),
            new PlaybackProgressStore(Path.Combine(_directory, "diagnostics-state.db")),
            headless: false,
            windowHandle: new IntPtr(1234),
            transportLease: null,
            videoOptions: new MpvWindowsVideoOptions());
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("warn", client.RequestedLogLevel);
        var diagnostics = session.GetNativeDiagnostics();
        Assert.True(diagnostics.SurfaceAttached);
        Assert.DoesNotContain(diagnostics.Properties, item => item.Name == "unrelated-property");
        Assert.DoesNotContain(diagnostics.Properties, item => item.Name == "cache-speed");
        Assert.Equal("gpu-next", diagnostics.Properties.Single(item => item.Name == "vo").Value);
        Assert.Equal(256, diagnostics.Properties.Single(item => item.Name == "video-params/primaries").Value!.Length);
        Assert.Equal(2, diagnostics.RecentLogs.Count);
        Assert.DoesNotContain("abc123", diagnostics.RecentLogs[0].Text);
        Assert.Equal("plain message", diagnostics.RecentLogs[1].Text);
    }

    [Fact]
    public async Task NativeDiagnosticsReportsDetachedSurfaceForHeadlessSessions()
    {
        var client = new FakeLibMpvClient();
        var mediaPath = Path.Combine(_directory, "diagnostics-headless.mkv");
        File.WriteAllBytes(mediaPath, []);

        await using var session = await LibMpvPlaybackSession.StartAsync(
            client,
            CreateEpisode(mediaPath),
            new AppSettings(),
            new PlaybackProgressStore(Path.Combine(_directory, "diagnostics-headless-state.db")),
            headless: true,
            windowHandle: null,
            transportLease: null,
            videoOptions: new MpvWindowsVideoOptions());

        Assert.False(session.GetNativeDiagnostics().SurfaceAttached);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task AudioSrcBypassAndCueEndMapToNativeOptions()
    {
        async Task<Dictionary<string, string>> StartWith(AppSettings settings, long? start = null, long? end = null)
        {
            var client = new FakeLibMpvClient();
            await using var session = await LibMpvPlaybackSession.StartAsync(
                client,
                CreateEpisode(Path.Combine(_directory, $"src-{Guid.NewGuid():N}.mkv")),
                settings,
                new PlaybackProgressStore(Path.Combine(_directory, $"src-{Guid.NewGuid():N}.db")),
                headless: true,
                windowHandle: null,
                transportLease: null,
                videoOptions: null,
                startPositionMs: start,
                endPositionMs: end);
            await session.DisposeAsync();
            return client.Options;
        }

        var software = await StartWith(new AppSettings(AudioSrcBypass: "software", AudioOutputSampleRate: 44100));
        Assert.Equal("44100", software["audio-samplerate"]);

        var direct = await StartWith(new AppSettings(AudioSrcBypass: "direct"));
        Assert.Equal("wasapi", direct["ao"]);
        Assert.Equal("yes", direct["ao-wasapi-exclusive"]);
        Assert.False(direct.ContainsKey("audio-samplerate"));

        var dspFallback = await StartWith(new AppSettings(
            AudioSrcBypass: "direct",
            AudioDsp: new AudioDspConfig(Enabled: true)));
        Assert.Equal("48000", dspFallback["audio-samplerate"]);
        Assert.False(dspFallback.ContainsKey("ao-wasapi-exclusive"));

        var system = await StartWith(new AppSettings(AudioSrcBypass: "system"));
        Assert.False(system.ContainsKey("audio-samplerate"));
        Assert.False(system.ContainsKey("ao-wasapi-exclusive"));

        var cue = await StartWith(new AppSettings(), start: 1_000, end: 2_000);
        Assert.Equal("1", cue["start"]);
        Assert.Equal("2", cue["end"]);
    }

    private static LibraryEpisode CreateEpisode(string mediaPath, IReadOnlyList<string>? subtitles = null) => new(
        1,
        "episode-uuid",
        "episode-progress",
        1,
        1,
        1,
        "Episode",
        mediaPath,
        TimeSpan.FromMinutes(24),
        subtitles ?? []);

    private static LibMpvNodeValue Tracks(params LibMpvNodeValue[] tracks) =>
        new(LibMpvNodeKind.Array, ArrayValue: tracks);

    private static LibMpvNodeValue Track(
        int id,
        string type,
        string? language = null,
        string? codec = null,
        bool selected = false,
        string? externalFileName = null) =>
        new(LibMpvNodeKind.Map, MapValue: new Dictionary<string, LibMpvNodeValue>
        {
            ["id"] = new(LibMpvNodeKind.Int64, Int64Value: id),
            ["type"] = new(LibMpvNodeKind.String, StringValue: type),
            ["lang"] = new(LibMpvNodeKind.String, StringValue: language),
            ["codec"] = new(LibMpvNodeKind.String, StringValue: codec),
            ["selected"] = new(LibMpvNodeKind.Flag, FlagValue: selected),
            ["external-filename"] = new(LibMpvNodeKind.String, StringValue: externalFileName),
        });

    private sealed class FakeLibMpvClient : ILibMpvClient
    {
        private readonly Queue<LibMpvEvent> _events;
        private bool _fileLoadedDelivered;

        public FakeLibMpvClient(params LibMpvEvent[] events) => _events = new(events);
        public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public LibMpvNodeValue? TrackList { get; set; }
        public int DisposeCount { get; private set; }
        public int SetPropertyString(string name, string value)
        {
            Properties[name] = value;
            return 0;
        }

        public int SetOptionString(string name, string value)
        {
            Options[name] = value;
            return 0;
        }
        public int Initialize() => 0;
        public string? RequestedLogLevel { get; private set; }
        public void RequestLogMessages(string minimumLevel) => RequestedLogLevel = minimumLevel;
        public int Command(IReadOnlyList<string> arguments)
        {
            Commands.Add(arguments.ToArray());
            return 0;
        }
        public string? GetPropertyString(string name) => Properties.GetValueOrDefault(name);
        public LibMpvNodeValue? GetPropertyNode(string name, uint format = 6) => name == "track-list" ? TrackList : null;
        public int ObserveProperty(ulong userdata, string name, uint format) => 0;
        public LibMpvEvent WaitEvent(double timeoutSeconds)
        {
            if (_events.TryDequeue(out var nativeEvent)) return nativeEvent;
            if (!_fileLoadedDelivered)
            {
                _fileLoadedDelivered = true;
                return new(8, 0, 0, IntPtr.Zero);
            }
            Thread.Sleep(1);
            return new(0, 0, 0, IntPtr.Zero);
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingProgressStore : IPlaybackProgressStore
    {
        public List<(long PositionMs, long DurationMs, bool Completed)> Saves { get; } = [];

        public PlaybackProgress? Get(string episodeKey) => null;

        public void Save(string episodeKey, long positionMs, long durationMs, bool completed = false) =>
            Saves.Add((positionMs, durationMs, completed));
    }

    private sealed class ThrowingProgressStore : IPlaybackProgressStore
    {
        public PlaybackProgress? Get(string episodeKey) => null;

        public void Save(string episodeKey, long positionMs, long durationMs, bool completed = false) =>
            throw new IOException("state.db is unavailable");
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
