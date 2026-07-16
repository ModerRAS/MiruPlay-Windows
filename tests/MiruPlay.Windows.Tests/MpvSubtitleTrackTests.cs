using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvSubtitleTrackTests
{
    [Fact]
    public void TrackListParserEnumeratesEmbeddedAndExternalSubtitleState()
    {
        using var document = JsonDocument.Parse("""
            [
              { "id": 1, "type": "video", "codec": "h264", "selected": true },
              { "id": 2, "type": "sub", "lang": "jpn", "title": "Japanese", "codec": "ass", "selected": false },
              { "id": 3, "type": "sub", "lang": "zh-CN", "title": "简体中文", "codec": "subrip", "external": true, "external-filename": "episode.zh-CN.srt", "selected": true }
            ]
            """);

        var tracks = MpvPlaybackSession.ParseSubtitleTracks(document.RootElement);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(2, tracks[0].Id);
        Assert.False(tracks[0].IsExternal);
        Assert.Equal("Japanese", tracks[0].DisplayLabel);
        Assert.Equal(3, tracks[1].Id);
        Assert.True(tracks[1].IsExternal);
        Assert.True(tracks[1].IsSelected);
        Assert.Equal("简体中文（外挂）", tracks[1].DisplayLabel);
    }

    [Fact]
    public async Task FailedIpcAttachmentTerminatesUnownedMpvProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"miruplay-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var startInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        var process = Process.Start(startInfo)!;
        var processId = process.Id;
        var episode = new LibraryEpisode(
            1,
            "attach-test",
            "attach-progress",
            1,
            1,
            1,
            "Attach test",
            "test.mkv",
            TimeSpan.FromSeconds(1),
            []);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MpvPlaybackSession.AttachAsync(
                    process,
                    $"missing-{Guid.NewGuid():N}",
                    episode,
                    new PlaybackProgressStore(Path.Combine(directory, "state.db")),
                    connectTimeoutMs: 100));

            Assert.Contains("无法建立控制连接", error.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        }
        finally
        {
            process.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("\"4\"", 4)]
    [InlineData("false", null)]
    [InlineData("\"no\"", null)]
    public void SidParserSupportsSelectedAndOffStates(string json, int? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, MpvPlaybackSession.ParseSubtitleTrackId(document.RootElement));
    }
}
