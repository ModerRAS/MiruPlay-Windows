using System.Globalization;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record FilenameMetadata(
    string? Title = null,
    int? Season = null,
    int? Episode = null,
    string? Group = null,
    string? Resolution = null,
    string? Source = null,
    string? Special = null);

public interface IAnimeFilenameParser
{
    FilenameMetadata Parse(string filename, int maxLength = 128);
}

public sealed record VideoClassification(string ShowName, int SeasonNumber, double? EpisodeNumber);

public interface IAnimeVideoClassifier
{
    VideoClassification Classify(string path, string fileName, string? parentName = null);
}

public sealed class AnimeVideoClassifier : IAnimeVideoClassifier
{
    private readonly IAnimeFilenameParser? _filenameParser;

    public AnimeVideoClassifier(IAnimeFilenameParser? filenameParser = null) => _filenameParser = filenameParser;

    public VideoClassification Classify(string path, string fileName, string? parentName = null)
    {
        var rules = VideoFilenameInference.Classify(fileName, parentName);
        var needsModel = rules.ShowName.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            rules.SeasonNumber == 1 || rules.EpisodeNumber is null;
        var model = needsModel ? _filenameParser?.Parse(Path.GetFileNameWithoutExtension(fileName)) : null;
        var title = !string.IsNullOrWhiteSpace(rules.ShowName) &&
            !rules.ShowName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? rules.ShowName
            : CleanModelTitle(model?.Title) ?? rules.ShowName;
        var season = rules.SeasonNumber > 1
            ? rules.SeasonNumber
            : model?.Season is > 0 ? model.Season.Value : rules.SeasonNumber;
        var episode = rules.EpisodeNumber ?? model?.Episode;
        return new VideoClassification(title, Math.Max(1, season), episode);
    }

    private static string? CleanModelTitle(string? value)
    {
        var title = value?.Trim();
        return string.IsNullOrWhiteSpace(title) || title.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : title;
    }
}

public static class SharedAnimeVideoClassifier
{
    private static readonly Lazy<IAnimeVideoClassifier> LazyInstance = new(CreateDefault);

    public static IAnimeVideoClassifier Instance => LazyInstance.Value;

    private static AnimeVideoClassifier CreateDefault() =>
        new(OnnxAnimeFilenameParser.CreateDefaultLazy());
}

internal static class ClassifierText
{
    public static int? ExtractNumber(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value, "\\d+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        if (match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number;
        foreach (var (character, numberValue) in new[]
        {
            ('一', 1), ('二', 2), ('三', 3), ('四', 4), ('五', 5),
            ('六', 6), ('七', 7), ('八', 8), ('九', 9), ('十', 10),
        })
        {
            if (value.Contains(character, StringComparison.Ordinal)) return numberValue;
        }
        return null;
    }
}
