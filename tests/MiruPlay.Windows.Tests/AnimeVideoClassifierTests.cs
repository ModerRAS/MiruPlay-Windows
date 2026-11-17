using System.Diagnostics.CodeAnalysis;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AnimeVideoClassifierTests
{
    [Fact]
    public void ModelFillsMissingSeasonAndEpisodeWithoutReplacingRuleTitle()
    {
        var classifier = new AnimeVideoClassifier(new StaticParser(new FilenameMetadata(
            Title: "Model Title", Season: 2, Episode: 7)));

        var result = classifier.Classify(
            "/library/Rule Title/S02/07.mkv",
            "07.mkv",
            "Rule Title");

        Assert.Equal("Rule Title", result.ShowName);
        Assert.Equal(2, result.SeasonNumber);
        Assert.Equal(7, result.EpisodeNumber);
    }

    [Fact]
    public void ModelSuppliesTitleWhenRulesHaveNoContext()
    {
        var classifier = new AnimeVideoClassifier(new StaticParser(new FilenameMetadata(
            Title: "葬送のフリーレン", Season: 1, Episode: 3)));

        var result = classifier.Classify("/raw/unknown.mkv", "unknown.mkv");

        Assert.Equal("葬送のフリーレン", result.ShowName);
        Assert.Equal(1, result.SeasonNumber);
        Assert.Equal(3, result.EpisodeNumber);
    }

    [Fact]
    public void OrganizerClassifierContractCanBeSharedByDirectoryAndCloudDriveCallers()
    {
        var result = ClassifyShared(new AnimeVideoClassifier(new StaticParser(new FilenameMetadata(Episode: 3))));

        Assert.Equal("Frieren", result.ShowName);
        Assert.Equal(3, result.EpisodeNumber);
    }

    [Theory]
    [InlineData("cdn.example.com")]
    [InlineData("2026-10-20")]
    [InlineData("A1%80%2F")]
    [InlineData("https:")]
    [InlineData("https://bgm.tv/subject/431767")]
    public void UrlMachineryParentNeverBecomesSeriesTitle(string parentName)
    {
        var classifier = new AnimeVideoClassifier(new StaticParser(new FilenameMetadata()));

        var result = classifier.Classify("https://cdn.example.com/2026-10-20/A1%80/01.mkv", "01.mkv", parentName);

        Assert.Equal("Unknown", result.ShowName);
    }

    [Fact]
    public void RealFolderNameStillSuppliesSeriesTitle()
    {
        var classifier = new AnimeVideoClassifier(new StaticParser(new FilenameMetadata()));

        var result = classifier.Classify("Frieren/01.mkv", "01.mkv", "Frieren");

        Assert.Equal("Frieren", result.ShowName);
    }

    [SuppressMessage("Performance", "CA1859")]
    private static VideoClassification ClassifyShared(IAnimeVideoClassifier shared) =>
        shared.Classify("/library/Frieren/03.mkv", "03.mkv", "Frieren");

    private sealed class StaticParser(FilenameMetadata metadata) : IAnimeFilenameParser
    {
        public FilenameMetadata Parse(string filename, int maxLength = 128) => metadata;
    }
}
