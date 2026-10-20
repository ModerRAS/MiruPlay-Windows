using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record MetadataSearchRequest(
    string Query,
    string Mode = "anime",
    string? Title = null,
    string? LocalizedTitle = null,
    string? OriginalTitle = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? FilePathSamples = null,
    string? MetadataTitle = null,
    int? YearHint = null,
    int? SeasonHint = null);

public sealed record MetadataSearchQuery(string Text, string Reason);
public sealed record MetadataSearchPlan(IReadOnlyList<MetadataSearchQuery> Queries, int? YearHint, int? SeasonHint);
public sealed record MetadataProviderCandidate(
    string Provider,
    string Id,
    string Title,
    string? LocalizedTitle,
    string? OriginalTitle,
    IReadOnlyList<string> Aliases,
    string MatchedQuery,
    int ProviderRank,
    float? ProviderScore,
    string? Summary,
    string? FirstAirDate,
    string? PosterUrl);
public sealed record AggregatedMetadataCandidate(
    string Mode,
    string Title,
    string? LocalizedTitle,
    string? OriginalTitle,
    string? Summary,
    string? FirstAirDate,
    string? PosterUrl,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<MetadataProviderCandidate> Providers,
    float RerankScore,
    string Recommendation,
    IReadOnlyList<string> Evidence);
public sealed record MetadataSearchResponse(
    MetadataSearchPlan Plan,
    IReadOnlyList<AggregatedMetadataCandidate> Candidates,
    IReadOnlyList<string> ProviderErrors,
    bool FromCache);

public sealed class MetadataSearchService(
    BangumiMetadataClient bangumi,
    TmdbMetadataClient tmdb,
    TvMazeMetadataClient tvMaze)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private const int CacheCapacity = 128;
    private static readonly Regex SeasonSuffix = new(@"(?i)\b(?:season\s*|s)(?<n>\d{1,2})(?=\b|e\d)|第\s*(?<cn>[一二三四五六七八九十\d]+)\s*[季期]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EpisodeNoise = new(@"(?i)(?:s\d{1,2}\s*e\d{1,3}|\b\d{1,3}x\d{1,3}\b|第\s*\d+\s*集|\b\d{3,4}p\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, MetadataSearchResponse Value)> _cache = new(StringComparer.Ordinal);

    public async Task<MetadataSearchResponse> SearchAsync(
        MetadataSearchRequest request,
        string? tmdbToken,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(request);
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tmdbToken?.Trim() ?? string.Empty)));
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.Mode.ToLowerInvariant()}|{request.YearHint}|{request.SeasonHint}|{tokenHash}|{string.Join('|', plan.Queries.Select(item => item.Text.ToLowerInvariant()))}")));
        lock (_cacheLock)
        {
            RemoveExpired();
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached.Value with { FromCache = true };
        }

        Task<(List<MetadataProviderCandidate> Candidates, List<string> Errors)>[] tasks = request.Mode switch
        {
            "anime" => [SearchBangumiAsync(plan, cancellationToken)],
            "drama" => [SearchTmdbAsync(plan, tmdbToken, cancellationToken), SearchTvMazeAsync(plan, cancellationToken)],
            _ => throw new ArgumentException(AppStrings.Get("search.mode"), nameof(request)),
        };
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var candidates = results.SelectMany(item => item.Candidates).ToList();
        var errors = results.SelectMany(item => item.Errors).ToList();
        var response = new MetadataSearchResponse(plan, Aggregate(request, plan, candidates), errors, false);
        if (errors.Count == 0)
        {
            lock (_cacheLock)
            {
                RemoveExpired();
                if (_cache.Count >= CacheCapacity)
                {
                    var oldest = _cache.MinBy(item => item.Value.Expires);
                    _cache.Remove(oldest.Key);
                }
                _cache[cacheKey] = (DateTimeOffset.UtcNow + CacheLifetime, response);
            }
        }
        return response;
    }

    public static MetadataSearchPlan Plan(MetadataSearchRequest request)
    {
        if (request.Mode is not ("anime" or "drama")) throw new ArgumentException(AppStrings.Get("search.mode"), nameof(request));
        var queries = new Dictionary<string, MetadataSearchQuery>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value, string reason)
        {
            var text = value?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 100) queries.TryAdd(text, new(text, reason));
        }
        void AddSeasonless(string? value, string reason)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var text = SeasonSuffix.Replace(value, " ");
            text = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            if (!text.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) Add(text, reason);
        }

        Add(request.Query, "manual");
        AddSeasonless(request.Query, "manual-seasonless");
        Add(request.Title, "title");
        AddSeasonless(request.Title, "title-seasonless");
        Add(request.LocalizedTitle, "localized");
        Add(request.OriginalTitle, "original");
        AddSeasonless(request.OriginalTitle, "original-seasonless");
        foreach (var alias in request.Aliases ?? []) Add(alias, "alias");
        Add(request.MetadataTitle, "cached-metadata");
        foreach (var path in request.FilePathSamples ?? [])
        {
            var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            Add(EpisodeNoise.Replace(Path.GetFileNameWithoutExtension(segments[^1]), " ").Trim(' ', '.', '_', '-'), "path");
            if (segments.Length > 1) Add(segments[^2], "path-parent");
        }
        var planned = queries.Values.Take(10).ToList();
        if (planned.Count == 0) throw new ArgumentException(AppStrings.Get("search.needQuery"), nameof(request));
        return new MetadataSearchPlan(planned, request.YearHint ?? planned.Select(item => ExtractYear(item.Text)).FirstOrDefault(value => value is not null),
            request.SeasonHint ?? planned.Select(item => ExtractSeason(item.Text)).FirstOrDefault(value => value is not null));
    }

    private async Task<(List<MetadataProviderCandidate>, List<string>)> SearchBangumiAsync(MetadataSearchPlan plan, CancellationToken cancellationToken)
    {
        var candidates = new List<MetadataProviderCandidate>();
        var errors = new List<string>();
        foreach (var query in plan.Queries)
        {
            try
            {
                var results = await bangumi.SearchAsync(query.Text, 5, cancellationToken).ConfigureAwait(false);
                candidates.AddRange(results.Select((item, index) => new MetadataProviderCandidate(
                    "Bangumi", item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Title, item.TitleCn, item.Title,
                    new[] { item.Title, item.TitleCn }.OfType<string>().ToArray(), query.Text,
                    index, null, item.Summary, item.AirDate, item.PosterUrl)));
            }
            catch (Exception error) when (IsProviderFailure(error, cancellationToken)) { errors.Add($"Bangumi: {error.Message}"); }
        }
        return (candidates, errors);
    }

    private async Task<(List<MetadataProviderCandidate>, List<string>)> SearchTmdbAsync(MetadataSearchPlan plan, string? token, CancellationToken cancellationToken)
    {
        var candidates = new List<MetadataProviderCandidate>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(token)) return (candidates, errors);
        foreach (var query in plan.Queries)
        {
            try
            {
                var results = await tmdb.SearchAsync(query.Text, token, plan.YearHint, 5, cancellationToken).ConfigureAwait(false);
                candidates.AddRange(results.Select((item, index) => new MetadataProviderCandidate(
                    "TMDB", item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Title, ContainsCjk(item.Title) ? item.Title : null, item.OriginalTitle,
                    new[] { item.Title, item.OriginalTitle }.OfType<string>().ToArray(), query.Text,
                    index, null, item.Summary, item.FirstAirDate, item.PosterUrl)));
            }
            catch (Exception error) when (IsProviderFailure(error, cancellationToken)) { errors.Add($"TMDB: {error.Message}"); }
        }
        return (candidates, errors);
    }

    private async Task<(List<MetadataProviderCandidate>, List<string>)> SearchTvMazeAsync(MetadataSearchPlan plan, CancellationToken cancellationToken)
    {
        var candidates = new List<MetadataProviderCandidate>();
        var errors = new List<string>();
        foreach (var query in plan.Queries)
        {
            try
            {
                var results = await tvMaze.SearchAsync(query.Text, 5, cancellationToken).ConfigureAwait(false);
                candidates.AddRange(results.Select((item, index) => new MetadataProviderCandidate(
                    "TVMaze", item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Title, ContainsCjk(item.Title) ? item.Title : null, item.OriginalTitle,
                    new[] { item.Title, item.OriginalTitle }.OfType<string>().ToArray(), query.Text,
                    index, null, item.Summary, item.FirstAirDate, item.PosterUrl)));
            }
            catch (Exception error) when (IsProviderFailure(error, cancellationToken)) { errors.Add($"TVMaze: {error.Message}"); }
        }
        return (candidates, errors);
    }

    private static List<AggregatedMetadataCandidate> Aggregate(
        MetadataSearchRequest request,
        MetadataSearchPlan plan,
        List<MetadataProviderCandidate> candidates)
    {
        var unique = candidates
            .GroupBy(item => (item.Provider, item.Id), StringTupleComparer.Instance)
            .Select(group => group.OrderBy(item => item.ProviderRank).First())
            .OrderByDescending(item => Completeness(item))
            .ToList();
        var clusters = new List<List<MetadataProviderCandidate>>();
        foreach (var candidate in unique)
        {
            var cluster = clusters.FirstOrDefault(items => items.Any(existing => ShouldCluster(existing, candidate)));
            if (cluster is null) clusters.Add([candidate]);
            else cluster.Add(candidate);
        }

        return clusters.Select(cluster =>
        {
            var titles = cluster.SelectMany(Titles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var representative = cluster.OrderByDescending(item => plan.Queries.Max(query => Similarity(query.Text, item.Title)))
                .ThenByDescending(Completeness).ThenBy(item => item.ProviderRank).First();
            var titleScore = cluster.Max(item => plan.Queries.Max(query => Titles(item).Max(value => Similarity(query.Text, value))));
            var yearScore = PlanYearScore(plan.YearHint, cluster.Select(item => item.FirstAirDate).OfType<string>()
                .Select(ExtractYear).Where(value => value.HasValue).Select(value => value!.Value));
            var rankScore = 1f / (1 + cluster.Min(item => item.ProviderRank));
            var completeness = cluster.Max(Completeness);
            var diversity = cluster.Select(item => item.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            var score = (titleScore * .65f + yearScore * .15f + rankScore * .1f + completeness * .05f + (diversity ? .05f : 0f));
            var evidence = new List<string>();
            if (titleScore >= .95f) evidence.Add(AppStrings.Get("search.exactTitle"));
            else if (titleScore >= .66f) evidence.Add(AppStrings.Get("search.similarTitle"));
            if (plan.YearHint is not null && yearScore >= .85f) evidence.Add(AppStrings.Get("search.yearClose"));
            if (diversity) evidence.Add(AppStrings.Get("search.converge"));
            if (completeness >= .6f) evidence.Add(AppStrings.Get("search.complete"));
            return new AggregatedMetadataCandidate(
                request.Mode,
                representative.Title,
                cluster.Select(item => item.LocalizedTitle).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                cluster.Select(item => item.OriginalTitle).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                cluster.Select(item => item.Summary).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                cluster.Select(item => item.FirstAirDate).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                cluster.Select(item => item.PosterUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                titles,
                cluster.OrderBy(item => ProviderOrder(item.Provider)).ThenBy(item => item.ProviderRank).ToList(),
                score,
                score >= .88f ? "AUTO_ACCEPT" : score >= .64f ? "REVIEW" : "LOW_CONFIDENCE",
                evidence);
        }).OrderByDescending(item => item.RerankScore).ToList();
    }

    private static bool ShouldCluster(MetadataProviderCandidate left, MetadataProviderCandidate right)
    {
        if (left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase)) return false;
        var similarity = Titles(left).SelectMany(a => Titles(right).Select(b => Similarity(a, b))).DefaultIfEmpty(0).Max();
        if (similarity < .66f) return false;
        var leftSeason = Titles(left).Select(ExtractSeason).FirstOrDefault(value => value is not null);
        var rightSeason = Titles(right).Select(ExtractSeason).FirstOrDefault(value => value is not null);
        if (leftSeason is not null && rightSeason is not null && leftSeason != rightSeason) return false;
        var leftYear = ExtractYear(left.FirstAirDate ?? string.Empty);
        var rightYear = ExtractYear(right.FirstAirDate ?? string.Empty);
        return leftYear is null || rightYear is null || Math.Abs(leftYear.Value - rightYear.Value) < 3;
    }

    private static IEnumerable<string> Titles(MetadataProviderCandidate item) =>
        new[] { item.Title, item.LocalizedTitle, item.OriginalTitle }.OfType<string>()
            .Concat(item.Aliases).Where(value => !string.IsNullOrWhiteSpace(value));

    private static float Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length > b.Length ? a : b;
        if (shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal)) return .9f;
        var ac = a.Where(char.IsLetterOrDigit).ToHashSet();
        var bc = b.Where(char.IsLetterOrDigit).ToHashSet();
        if (ac.Count == 0 || bc.Count == 0) return 0;
        var cjk = ac.Any(IsCjk) && bc.Any(IsCjk) && Math.Min(ac.Count, bc.Count) >= 3
            ? ac.Intersect(bc).Count() / (float)Math.Max(ac.Count, bc.Count)
            : 0f;
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(value => value.Length > 1).ToHashSet(StringComparer.Ordinal);
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(value => value.Length > 1).ToHashSet(StringComparer.Ordinal);
        var token = at.Count == 0 || bt.Count == 0 ? 0 : at.Intersect(bt).Count() / (float)Math.Min(at.Count, bt.Count);
        return Math.Max(cjk, token >= .6f ? .72f : 0f);
    }

    private static string Normalize(string value)
    {
        var seasonless = SeasonSuffix.Replace(value, " ");
        var normalized = seasonless.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized) builder.Append(char.IsLetterOrDigit(character) || IsCjk(character) ? character : ' ');
        return Regex.Replace(builder.ToString(), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static float Completeness(MetadataProviderCandidate item) =>
        new[] { !string.IsNullOrWhiteSpace(item.LocalizedTitle), !string.IsNullOrWhiteSpace(item.OriginalTitle),
            !string.IsNullOrWhiteSpace(item.Summary), !string.IsNullOrWhiteSpace(item.FirstAirDate), !string.IsNullOrWhiteSpace(item.PosterUrl) }
            .Count(value => value) / 5f;

    private static float PlanYearScore(int? year, IEnumerable<int> years)
    {
        if (year is null) return 0;
        var distance = years.Select(value => Math.Abs(value - year.Value)).DefaultIfEmpty(int.MaxValue).Min();
        return distance switch { 0 => 1f, 1 => .85f, 2 => .65f, int.MaxValue => 0f, _ => .2f };
    }

    private static int ProviderOrder(string provider) => provider switch { "Bangumi" => 0, "TMDB" => 1, _ => 2 };
    private static bool IsCjk(char value) => value is >= '\u4e00' and <= '\u9fff';
    private static bool ContainsCjk(string value) => value.Any(IsCjk);
    private static int? ExtractYear(string value)
    {
        var match = Regex.Match(value, @"\b(?:19|20)\d{2}\b");
        return match.Success ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
    private static int? ExtractSeason(string value)
    {
        var match = SeasonSuffix.Match(value);
        if (!match.Success) return null;
        var number = match.Groups["n"].Success ? match.Groups["n"].Value : match.Groups["cn"].Value;
        if (int.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return number switch
        {
            "十" => 10,
            "十一" => 11,
            "十二" => 12,
            "十三" => 13,
            "十四" => 14,
            "十五" => 15,
            "十六" => 16,
            "十七" => 17,
            "十八" => 18,
            "十九" => 19,
            "二十" => 20,
            _ => null,
        };
    }
    private static bool IsProviderFailure(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException ||
        error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _cache.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray()) _cache.Remove(key);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Provider, string Id)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Provider, string Id) x, (string Provider, string Id) y) =>
            x.Provider.Equals(y.Provider, StringComparison.OrdinalIgnoreCase) && x.Id.Equals(y.Id, StringComparison.Ordinal);
        public int GetHashCode((string Provider, string Id) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider), value.Id);
    }
}
