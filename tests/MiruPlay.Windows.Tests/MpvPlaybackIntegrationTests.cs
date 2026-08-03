using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvPlaybackIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-mpv-ipc-{Guid.NewGuid():N}");

    public MpvPlaybackIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealMpvSupportsCommandsProgressNaturalEofAndImmediateStop()
    {
        var mpvPath = Environment.GetEnvironmentVariable("MIRUPLAY_MPV_PATH");
        var mediaPath = Environment.GetEnvironmentVariable("MIRUPLAY_MPV_SMOKE_MEDIA");
        if (mpvPath is null || mediaPath is null || !File.Exists(mpvPath) || !File.Exists(mediaPath))
        {
            Assert.False(
                Environment.GetEnvironmentVariable("MIRUPLAY_REQUIRE_MPV_SMOKE") == "1",
                "The required real-mpv smoke paths are missing.");
            return;
        }

        await using (var remoteServer = new AuthenticatedMediaServer(
            await File.ReadAllBytesAsync(mediaPath),
            "alice",
            "p@ss"))
        {
            var remoteStore = new PlaybackProgressStore(Path.Combine(_directory, "remote-state.db"));
            var remoteEpisode = Episode(3, "remote-episode", "remote-progress", "Remote IPC smoke", remoteServer.MediaUrl) with
            {
                SourceId = 3,
            };
            using var webDav = new WebDavMlipClient(
                cacheRoot: Path.Combine(_directory, "webdav-cache"),
                minimumRequestInterval: TimeSpan.Zero);
            var credential = new MediaSourceCredential("alice", "p@ss");
            var playbackProxy = new WebDavPlaybackProxy(
                webDav,
                new Uri(new Uri(remoteServer.MediaUrl), "/").AbsoluteUri,
                credential,
                remoteEpisode);
            var remoteSession = await MpvPlayerLauncher.PlayAsync(
                remoteEpisode,
                new AppSettings(PlayerPath: mpvPath),
                remoteStore,
                headless: true,
                playbackProxy: playbackProxy);

            Assert.NotNull(remoteSession);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.True(remoteSession.IsActive);
            await remoteSession.ExecuteCommandAsync(new PlaybackControlCommand("speed", Speed: 4f));
            await remoteSession.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(remoteSession.WasCompleted);
            Assert.True(remoteServer.AuthorizedRequests > 0);
            Assert.IsType<PlaybackProgress>(remoteStore.Get(remoteEpisode.ProgressKey));
            var secretsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiruPlay",
                "runtime-secrets");
            Assert.Empty(Directory.Exists(secretsDirectory)
                ? Directory.EnumerateFiles(secretsDirectory, "mpv-auth-*.conf")
                : []);
        }
        await Task.Delay(TimeSpan.FromSeconds(3));

        var naturalStore = new PlaybackProgressStore(Path.Combine(_directory, "state.db"));
        var subtitlePath = Path.Combine(_directory, "ipc-smoke.zh-CN.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:00,000 --> 00:00:10,000\nMiruPlay subtitle smoke\n");
        var naturalEpisode = Episode(1, "smoke-episode", "smoke-progress", "IPC smoke", mediaPath, subtitlePath);
        var naturalSession = await MpvPlayerLauncher.PlayAsync(
            naturalEpisode,
            new AppSettings(PlayerPath: mpvPath),
            naturalStore,
            headless: true);

        Assert.NotNull(naturalSession);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(
            naturalSession.IsActive,
            naturalSession.Completion.Exception?.ToString() ?? $"Session ended at {naturalSession.PositionMs}/{naturalSession.DurationMs}ms.");
        var externalSubtitle = Assert.Single(naturalSession.SubtitleTracks, track => track.IsExternal);
        Assert.Equal("ipc-smoke.zh-CN.srt", Path.GetFileName(externalSubtitle.ExternalFileName));
        if (Environment.GetEnvironmentVariable("MIRUPLAY_EXPECT_EMBEDDED_SUBTITLE") == "1")
        {
            Assert.Contains(naturalSession.SubtitleTracks, track => !track.IsExternal && track.Language == "jpn");
        }
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("subtitle", SubtitleTrackId: externalSubtitle.Id));
        Assert.Equal(externalSubtitle.Id, naturalSession.SelectedSubtitleTrackId);
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("subtitle"));
        Assert.Null(naturalSession.SelectedSubtitleTrackId);
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("pause"));
        Assert.True(naturalSession.IsPaused);
        Assert.False(naturalSession.IsPlaying);
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("seek", PositionMs: 5_000));
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("seek_relative", DeltaMs: -2_000));
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("speed", Speed: 1.5f));
        await naturalSession.ExecuteCommandAsync(new PlaybackControlCommand("resume"));
        Assert.False(naturalSession.IsPaused);
        Assert.True(naturalSession.IsPlaying);
        await naturalSession.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var naturalProgress = Assert.IsType<PlaybackProgress>(naturalStore.Get(naturalEpisode.ProgressKey));
        Assert.True(naturalSession.WasCompleted);
        Assert.True(naturalProgress.PositionMs >= 10_000, $"Expected IPC position >= 10s, got {naturalProgress.PositionMs}ms.");
        Assert.True(naturalProgress.DurationMs >= 17_000, $"Expected duration >= 17s, got {naturalProgress.DurationMs}ms.");
        await Task.Delay(TimeSpan.FromSeconds(3));

        var stopStore = new PlaybackProgressStore(Path.Combine(_directory, "stop-state.db"));
        var stopEpisode = Episode(2, "stop-episode", "stop-progress", "IPC stop smoke", mediaPath);
        var stopSession = await MpvPlayerLauncher.PlayAsync(
            stopEpisode,
            new AppSettings(PlayerPath: mpvPath),
            stopStore,
            headless: true);

        Assert.NotNull(stopSession);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(
            stopSession.IsActive,
            stopSession.Completion.Exception?.ToString() ?? $"Session ended at {stopSession.PositionMs}/{stopSession.DurationMs}ms.");
        await stopSession.ExecuteCommandAsync(new PlaybackControlCommand("seek", PositionMs: 17_000));
        await stopSession.ExecuteCommandAsync(new PlaybackControlCommand("stop"));
        await stopSession.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var stopProgress = Assert.IsType<PlaybackProgress>(stopStore.Get(stopEpisode.ProgressKey));
        Assert.False(stopSession.IsActive);
        Assert.False(stopSession.WasCompleted);
        Assert.Equal(17_000, stopProgress.PositionMs);
        await stopSession.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealMpvAcceptsPerChannelLinearPhaseDspThroughItsOwnIpcSession()
    {
        var mpvPath = Environment.GetEnvironmentVariable("MIRUPLAY_MPV_PATH");
        var mediaPath = Environment.GetEnvironmentVariable("MIRUPLAY_MPV_SMOKE_MEDIA");
        if (mpvPath is null || mediaPath is null || !File.Exists(mpvPath) || !File.Exists(mediaPath))
        {
            Assert.False(
                Environment.GetEnvironmentVariable("MIRUPLAY_REQUIRE_MPV_DSP_SMOKE") == "1",
                "The required real-mpv DSP smoke paths are missing.");
            return;
        }

        var episode = Episode(4, "dsp-smoke-episode", "dsp-smoke-progress", "DSP IPC smoke", mediaPath);
        var settings = new AppSettings(
            PlayerPath: mpvPath,
            AudioDsp: new AudioDspConfig(
                true,
                "linear-stereo",
                [new AudioDspPreset(
                    "linear-stereo",
                    "Linear stereo",
                    PhaseMode: AudioDspPhaseMode.Linear,
                    FirQuality: AudioDspFirQuality.Low,
                    Rules:
                    [
                        new(AudioDspChannelTarget.Left, [new(GainDb: 3, FrequencyHz: 1_000, Q: 1)]),
                        new(AudioDspChannelTarget.Right, [new(GainDb: -3, FrequencyHz: 1_000, Q: 1)]),
                    ])]));
        var store = new PlaybackProgressStore(Path.Combine(_directory, "dsp-state.db"));
        await using var session = await MpvPlayerLauncher.PlayAsync(
            episode,
            settings,
            store,
            headless: true) ?? throw new InvalidOperationException("Real mpv DSP smoke did not create a session.");

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(session.IsActive, session.LastError);
        var graph = AudioDspFilterGraphCompiler.Compile(
            settings.AudioDsp!,
            AudioDspChannelLayout.Stereo,
            48_000);
        await session.ApplyAudioDspAsync(graph);
        var actualAf = await session.GetAudioFilterGraphAsync();

        Assert.NotNull(actualAf);
        Assert.Contains("channelsplit", actualAf, StringComparison.Ordinal);
        Assert.Equal(2, actualAf.Split("firequalizer", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(actualAf, "delay="));
        Assert.Equal(graph.AfValue, session.AppliedAudioDsp?.AfValue);
    }

    private static LibraryEpisode Episode(
        long id,
        string uuid,
        string progressKey,
        string title,
        string mediaPath,
        params string[] subtitlePaths) => new(
        id,
        uuid,
        progressKey,
        1,
        1,
        1,
        title,
        mediaPath,
        TimeSpan.FromSeconds(18),
        subtitlePaths);

    private sealed class AuthenticatedMediaServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly byte[] _media;
        private readonly string _authorization;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;
        private int _authorizedRequests;

        public AuthenticatedMediaServer(byte[] media, string username, string password)
        {
            _media = media;
            _authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
            _listener.Start();
            var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            MediaUrl = $"http://127.0.0.1:{port}/ipc-smoke.mp4";
            _serverTask = ServeAsync();
        }

        public string MediaUrl { get; }
        public int AuthorizedRequests => Volatile.Read(ref _authorizedRequests);

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (Exception error) when (
                _cancellation.IsCancellationRequested &&
                error is OperationCanceledException or System.Net.Sockets.SocketException)
            {
                // Cancellation or TcpListener.Stop interrupts the pending accept.
            }
            _cancellation.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_cancellation.Token);
                string? authorization = null;
                string? range = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cancellation.Token)))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        authorization = line["Authorization:".Length..].Trim();
                    }
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        range = line["Range:".Length..].Trim();
                    }
                }
                if (authorization != _authorization)
                {
                    await WriteHeaderAsync(stream, "401 Unauthorized", 0, null);
                    continue;
                }
                Interlocked.Increment(ref _authorizedRequests);
                var start = ParseRangeStart(range);
                var length = _media.Length - start;
                var status = start > 0 ? "206 Partial Content" : "200 OK";
                var contentRange = start > 0 ? $"bytes {start}-{_media.Length - 1}/{_media.Length}" : null;
                await WriteHeaderAsync(stream, status, length, contentRange);
                if (requestLine?.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase) != true)
                {
                    await stream.WriteAsync(_media.AsMemory(start, length), _cancellation.Token);
                }
            }
        }

        private static int ParseRangeStart(string? range)
        {
            if (range is null || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return 0;
            var value = range["bytes=".Length..].Split('-', 2)[0];
            return int.TryParse(value, out var start) ? start : 0;
        }

        private static async Task WriteHeaderAsync(
            Stream stream,
            string status,
            int length,
            string? contentRange)
        {
            var header = $"HTTP/1.1 {status}\r\n" +
                "Content-Type: video/mp4\r\n" +
                "Accept-Ranges: bytes\r\n" +
                $"Content-Length: {length}\r\n" +
                (contentRange is null ? "" : $"Content-Range: {contentRange}\r\n") +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header));
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
