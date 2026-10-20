using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MusicLibraryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-music-{Guid.NewGuid():N}");

    public MusicLibraryServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ScanGroupsCueVirtualTracksAndLoneAudioByDirectory()
    {
        var album = Path.Combine(_root, "Symphony");
        Directory.CreateDirectory(album);
        File.WriteAllBytes(Path.Combine(album, "symphony.flac"), []);
        File.WriteAllText(Path.Combine(album, "symphony.cue"), """
            TITLE "Symphony No. 5"
            FILE "symphony.flac" WAVE
              TRACK 01 AUDIO
                TITLE "Allegro"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Andante"
                INDEX 01 05:00:00
            """);
        File.WriteAllBytes(Path.Combine(album, "cover.jpg"), []);
        var singles = Path.Combine(_root, "Singles");
        Directory.CreateDirectory(singles);
        File.WriteAllBytes(Path.Combine(singles, "song.mp3"), []);

        var service = new MusicLibraryService();
        var albums = service.Scan(_root);

        Assert.Equal(2, albums.Count);
        var symphony = Assert.Single(albums, item => item.Title == "Symphony");
        Assert.Equal(2, symphony.Tracks.Count);
        Assert.All(symphony.Tracks, track => Assert.True(track.FromCue));
        Assert.Equal("Allegro", symphony.Tracks[0].Title);
        Assert.Equal(symphony.Tracks[1].StartMs, symphony.Tracks[0].EndMs);
        Assert.Equal(-1, symphony.Tracks[1].EndMs);
        var single = Assert.Single(Assert.Single(albums, item => item.Title == "Singles").Tracks);
        Assert.False(single.FromCue);
        Assert.Equal("song", single.Title);
        // Cached result is reused without rescanning.
        Assert.Same(albums, service.Scan(_root));
    }

    [Fact]
    public void UnmatchedCueFilesFallBackToWholeFileTracks()
    {
        File.WriteAllBytes(Path.Combine(_root, "orphan.flac"), []);
        File.WriteAllText(Path.Combine(_root, "orphan.cue"), "TITLE \"Orphan\"\n");

        var tracks = Assert.Single(new MusicLibraryService().Scan(_root)).Tracks;

        Assert.Single(tracks);
        Assert.False(tracks[0].FromCue);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
