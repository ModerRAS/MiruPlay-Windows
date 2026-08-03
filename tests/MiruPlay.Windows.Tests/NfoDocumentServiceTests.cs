using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class NfoDocumentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miruplay-nfo-" + Guid.NewGuid().ToString("N"));

    public NfoDocumentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EpisodeRoundTripEscapesXmlAndConvertsResumeMinutes()
    {
        var media = Path.Combine(_root, "Show", "01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(media)!);
        File.WriteAllText(media, "video");
        var service = new NfoDocumentService(_root);

        service.WriteEpisode(media, new NfoEpisodeMetadata(
            Title: "A < B & C",
            ShowTitle: "Show",
            Season: 2,
            Episode: 3,
            Plot: "quoted \"plot\"",
            ResumePositionMs: 90_000,
            UniqueIds: [new NfoUniqueId("bangumi", "431767", true)]));

        var path = service.EpisodePath(media);
        var text = File.ReadAllText(path);
        var value = service.ReadEpisode(path);

        Assert.Contains("&lt;", text, StringComparison.Ordinal);
        Assert.Contains("&amp;", text, StringComparison.Ordinal);
        Assert.Equal("A < B & C", value.Title);
        Assert.Equal(2, value.Season);
        Assert.Equal(3, value.Episode);
        Assert.Equal(90_000, value.ResumePositionMs);
        Assert.Equal("431767", Assert.Single(value.UniqueIds!).Value);
    }

    [Fact]
    public void WatchProgressWritesBackupAndStaysWithinRoot()
    {
        var media = Path.Combine(_root, "01.mkv");
        File.WriteAllText(media, "video");
        var service = new NfoDocumentService(_root);
        service.WriteEpisode(media, new NfoEpisodeMetadata(Title: "Episode"));

        service.UpdateWatchProgress(media, 30_000, new DateTimeOffset(2026, 7, 14, 1, 2, 3, TimeSpan.Zero));

        Assert.True(File.Exists(service.EpisodePath(media) + ".bak"));
        Assert.Equal(30_000, service.ReadEpisode(service.EpisodePath(media)).ResumePositionMs);
        Assert.Throws<InvalidDataException>(() => service.WriteEpisode(Path.Combine(_root, "..", "outside.mkv"), new NfoEpisodeMetadata()));
    }

    [Fact]
    public void SecureXmlReaderRejectsExternalEntityDocuments()
    {
        var nfo = Path.Combine(_root, "episode.nfo");
        File.WriteAllText(nfo, "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///secret\">]><episodedetails><title>&xxe;</title></episodedetails>");
        var service = new NfoDocumentService(_root);

        Assert.ThrowsAny<Exception>(() => service.ReadEpisode(nfo));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
