using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class ExternalAudioPathMatcherTests
{
    [Fact]
    public void FindsOnlySupportedAudioWithTheSameVideoStemAndDirectory()
    {
        var matches = ExternalAudioPathMatcher.Find(
            "Season 01/Show.mkv",
            [
                "Season 01/Show.m4a",
                "Season 01/Show.en.flac",
                "Season 01/Show - Japanese.ac3",
                "Season 01/Showcase.en.flac",
                "Season 02/Show.en.flac",
                "Season 01/Show.en.srt",
            ]);

        Assert.Equal(
            ["Season 01/Show - Japanese.ac3", "Season 01/Show.en.flac", "Season 01/Show.m4a"],
            matches);
    }

    [Fact]
    public void FindLocalSiblingsDiscoversOnlyTheSameDirectoryAndIgnoresFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"miruplay-audio-sidecar-{Guid.NewGuid():N}");
        var nested = Path.Combine(directory, "Season 01");
        Directory.CreateDirectory(nested);
        var video = Path.Combine(nested, "Show.mkv");
        File.WriteAllBytes(video, []);
        File.WriteAllBytes(Path.Combine(nested, "Show.en.m4a"), []);
        File.WriteAllBytes(Path.Combine(nested, "Showcase.en.flac"), []);
        File.WriteAllBytes(Path.Combine(nested, "Show.en.srt"), []);
        File.WriteAllBytes(Path.Combine(directory, "Show.en.m4a"), []);
        try
        {
            var matches = ExternalAudioPathMatcher.FindLocalSiblings(video);
            var match = Assert.Single(matches);
            Assert.Equal($"{nested.Replace('\\', '/')}/Show.en.m4a", match);
            Assert.Empty(ExternalAudioPathMatcher.FindLocalSiblings(Path.Combine(nested, "missing.mkv")));
            Assert.Empty(ExternalAudioPathMatcher.FindLocalSiblings("https://media.test/Show.mkv"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
