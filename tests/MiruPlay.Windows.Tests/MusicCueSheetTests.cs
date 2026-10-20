using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MusicCueSheetTests
{
    private const string Sample = """
        REM GENRE Classical
        PERFORMER "Berlin Philharmonic"
        TITLE "Symphony No. 5"
        FILE "symphony.flac" WAVE
          TRACK 01 AUDIO
            TITLE "Allegro con brio"
            PERFORMER "Karajan"
            INDEX 00 00:00:00
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "Andante con moto"
            INDEX 00 07:23:10
            INDEX 01 07:24:00
          TRACK 03 AUDIO
            TITLE "Broken"
            INDEX 01 99:99:99
        """;

    [Fact]
    public void ParseReadsFileTitlesAndPregapSafeStarts()
    {
        var sheet = MusicCueSheet.Parse(Sample, "Fallback Album");

        Assert.Equal("symphony.flac", sheet.File);
        Assert.Equal("Symphony No. 5", sheet.Title);
        Assert.Equal("Berlin Philharmonic", sheet.Performer);
        Assert.Equal([1, 2], sheet.Tracks.Select(track => track.Number).ToList());
        Assert.Equal(0, sheet.Tracks[0].StartMs);
        Assert.Equal(((7 * 60 + 24) * 1_000), sheet.Tracks[1].StartMs);
        Assert.Equal("Karajan", sheet.Tracks[0].Performer);
        Assert.Equal("", sheet.Tracks[1].Performer);
        Assert.Equal("Berlin Philharmonic", MusicCueSheet.Expand("/music/symphony.flac", sheet, 600_000)[1].Performer);
    }

    [Fact]
    public void ExpandBoundsTracksByNextStartAndFileEnd()
    {
        var sheet = MusicCueSheet.Parse(Sample);
        var tracks = MusicCueSheet.Expand("/music/symphony.flac", sheet, 600_000);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(0, tracks[0].StartMs);
        Assert.Equal(tracks[1].StartMs, tracks[0].EndMs);
        Assert.Equal(600_000, tracks[1].EndMs);
        Assert.Equal("Allegro con brio", tracks[0].Title);
    }

    [Fact]
    public void ExpandLeavesLastTrackOpenEndedWithoutDuration()
    {
        var sheet = MusicCueSheet.Parse(Sample);
        var tracks = MusicCueSheet.Expand("/music/symphony.flac", sheet, 0);

        Assert.Equal(-1, tracks[^1].EndMs);
    }

    [Fact]
    public void OversizedInputIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => MusicCueSheet.Parse(new string('x', 1 * 1024 * 1024 + 1)));
    }
}
