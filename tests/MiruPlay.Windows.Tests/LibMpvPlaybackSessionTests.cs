using MiruPlay.Windows.Services;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Tests;

public sealed class LibMpvPlaybackSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-libmpv-session-{Guid.NewGuid():N}");

    public LibMpvPlaybackSessionTests() => Directory.CreateDirectory(_directory);

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
    }

    private sealed class FakeLibMpvClient : ILibMpvClient
    {
        public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> Commands { get; } = [];
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
        public int Command(IReadOnlyList<string> arguments)
        {
            Commands.Add(arguments.ToArray());
            return 0;
        }
        public string? GetPropertyString(string name) => Properties.GetValueOrDefault(name);
        public LibMpvNodeValue? GetPropertyNode(string name, uint format = 6) => null;
        public int ObserveProperty(ulong userdata, string name, uint format) => 0;
        public LibMpvEvent WaitEvent(double timeoutSeconds) => new(8, 0, 0, IntPtr.Zero);
        public void Dispose() { }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
