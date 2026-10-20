using System.Globalization;
using System.Text.RegularExpressions;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public static class LibraryOrganization
{
    public static readonly IReadOnlyList<string> ArrangementOptions = ["TITLE", "RELEASE_SEASON"];
    private static readonly Regex TitleSeparators = new(@"[._\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AirDateParts = new(@"([0-9]{4})\D+([0-9]{1,2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<LibrarySeries> Apply(IReadOnlyList<LibrarySeries> series, AppSettings settings)
    {
        var organized = settings.MergeSameAnimeEnabled ? Merge(series) : series;
        return settings.PosterWallArrangement == "RELEASE_SEASON"
            ? organized.OrderByDescending(item => ReleaseSeasonKey(item) ?? 0)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList()
            : organized.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static int? ReleaseSeasonKey(LibrarySeries series)
    {
        var match = AirDateParts.Match(series.AirDate ?? string.Empty);
        if (!match.Success) return null;
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12) return null;
        return year * 100 + (month >= 9 ? 9 : month >= 7 ? 7 : month >= 4 ? 4 : 1);
    }

    public static string ReleaseSeasonTitle(LibrarySeries series) => ReleaseSeasonKey(series) is int key
        ? AppStrings.Get("lib.seasonBucket", key / 100, key % 100)
        : AppStrings.Get("lib.unknownDate");

    private static List<LibrarySeries> Merge(IReadOnlyList<LibrarySeries> series)
    {
        // ponytail: quadratic grouping runs on catalog/settings changes; index identities if large catalogs make it measurable.
        var groups = new List<List<LibrarySeries>>();
        foreach (var item in series.DistinctBy(item => item.ApiId))
        {
            var matches = groups.Where(group => CanMerge(group, [item])).ToList();
            if (matches.Count == 0)
            {
                groups.Add([item]);
                continue;
            }
            var target = matches[0];
            target.Add(item);
            foreach (var other in matches.Skip(1).Where(other => CanMerge(target, other)))
            {
                target.AddRange(other);
                groups.Remove(other);
            }
        }
        return groups.Select(MergeGroup).ToList();
    }

    private static bool CanMerge(IReadOnlyList<LibrarySeries> left, IReadOnlyList<LibrarySeries> right) =>
        !left.Any(first => right.Any(second => HasConflictingId(first, second))) &&
        left.Any(first => right.Any(second => IdentityKeys(first).Intersect(IdentityKeys(second), StringComparer.OrdinalIgnoreCase).Any()));

    private static bool HasConflictingId(LibrarySeries left, LibrarySeries right) =>
        left.ExternalIds.Any(first => right.ExternalIds.Any(second =>
            first.Provider.Equals(second.Provider, StringComparison.OrdinalIgnoreCase) &&
            !first.Value.Equals(second.Value, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> IdentityKeys(LibrarySeries series)
    {
        foreach (var id in series.ExternalIds.Where(id => !string.IsNullOrWhiteSpace(id.Value) && id.Value != "0"))
            yield return $"{id.Provider}:{id.Value}";
        foreach (var title in new[] { series.Title, series.OriginalTitle })
        {
            var normalized = TitleSeparators.Replace(title ?? string.Empty, " ").Trim();
            if (normalized.Length > 0 && !normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                yield return $"title:{normalized}";
        }
    }

    private static LibrarySeries MergeGroup(List<LibrarySeries> group)
    {
        if (group.Count == 1) return group[0];
        var best = group.MaxBy(item =>
            (item.PosterPath is null ? 0 : 8) +
            (string.IsNullOrWhiteSpace(item.Summary) ? 0 : 2) +
            (item.ExternalIds.Count > 0 ? 2 : 0) +
            Math.Min(item.Episodes.Count, 100))!;
        return best with
        {
            Id = group[0].Id,
            Uuid = group[0].Uuid,
            AlternateApiIds = group.SelectMany(item => item.AlternateApiIds.Append(item.ApiId))
                .Where(id => id != group[0].ApiId).Distinct(StringComparer.Ordinal).ToList(),
            Episodes = group.SelectMany(item => item.Episodes).DistinctBy(item => item.ProgressKey)
                .OrderBy(item => item.Season).ThenBy(item => item.Number)
                .ThenBy(item => item.MediaPath, StringComparer.OrdinalIgnoreCase).ToList(),
            Extras = group.SelectMany(item => item.Extras).DistinctBy(item => item.MediaPath, StringComparer.OrdinalIgnoreCase).ToList(),
            Genres = group.SelectMany(item => item.Genres).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList(),
            ExternalIds = group.SelectMany(item => item.ExternalIds).Distinct().ToList(),
        };
    }
}
