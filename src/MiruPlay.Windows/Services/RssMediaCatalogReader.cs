using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed class RssMediaCatalogReader
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(10);
    private static readonly Regex EpisodeSuffix = new(
        @"^(?<series>.*?)\s+-\s+(?<episode>\d{1,4})(?:\s*\[.*)?\.[A-Za-z0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex GroupPrefix = new(@"^(?:\[[^\]]+\]\s*)+", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SeasonSuffix = new(@"\s*(?:[Ss]eason\s*\d+|第[0-9一二三四五六七八九十百]+\s*季)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SeasonEnglish = new(@"[Ss]eason\s*(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SeasonTagged = new(@"[Ss](\d+)\s*E", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SeasonChinese = new(@"第\s*([0-9一二三四五六七八九十百]+)\s*季", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly RssFeedClient _client;
    private readonly Func<CloudDriveAutomationConfig>? _getProxyConfig;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, LibraryCatalog Catalog)> _cache = new(StringComparer.Ordinal);

    public RssMediaCatalogReader(
        RssFeedClient? client = null,
        Func<CloudDriveAutomationConfig>? getProxyConfig = null)
    {
        _client = client ?? new RssFeedClient();
        _getProxyConfig = getProxyConfig;
    }

    public async Task<LibraryCatalog> LoadAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        var feedUri = ValidateFeedUri(feedUrl);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(feedUri.AbsoluteUri, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached.Catalog;
        }

        var proxy = _getProxyConfig?.Invoke();
        var items = await _client.FetchAsync(
            feedUri.AbsoluteUri,
            proxy?.RssProxyEnabled ?? false,
            proxy?.RssProxyHost ?? string.Empty,
            proxy?.RssProxyPort ?? 1080,
            cancellationToken).ConfigureAwait(false);
        var catalog = BuildCatalog(feedUri.AbsoluteUri, items);
        if (catalog.Series.Count == 0) throw new InvalidDataException(AppStrings.Get("rss.noPlayable"));
        lock (_cacheLock) _cache[feedUri.AbsoluteUri] = (DateTimeOffset.UtcNow + SnapshotTtl, catalog);
        return catalog;
    }

    public void Invalidate(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)) return;
        lock (_cacheLock) _cache.Remove(uri.AbsoluteUri);
    }

    internal static LibraryCatalog BuildCatalog(string feedUrl, IReadOnlyList<RssFeedItem> items)
    {
        var feedUri = ValidateFeedUri(feedUrl);
        var playable = items
            .Select(item => (Item: item, Url: SelectMediaUrl(item)))
            .Where(pair => pair.Url is not null)
            .Select(pair => new ParsedItem(pair.Item, pair.Url!, ParseEpisode(pair.Item.Title)))
            .ToList();
        var groups = playable
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Item.Category)
                ? item.Episode?.SeriesTitle ?? item.Item.GuidValue ?? item.Url
                : item.Item.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().Episode?.SeriesTitle ?? group.First().Item.Category ?? group.First().Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        long seriesId = 0;
        long episodeId = 0;
        var series = groups.Select(group =>
        {
            var ordered = group
                .OrderBy(item => item.Episode?.Season ?? 1)
                .ThenBy(item => item.Episode?.Number ?? int.MaxValue)
                .ThenBy(item => item.Item.PublishedAtMs)
                .ThenBy(item => item.Item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var seriesTitle = ordered.Select(item => item.Episode?.SeriesTitle)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? ordered[0].Item.Title;
            var episodes = ordered.Select((item, index) =>
            {
                var guid = string.IsNullOrWhiteSpace(item.Item.GuidValue) ? item.Url : item.Item.GuidValue.Trim();
                var key = ProgressKey(feedUri.AbsoluteUri, guid);
                var number = item.Episode?.Number ?? index + 1;
                return new LibraryEpisode(
                    ++episodeId,
                    StableId(guid),
                    key,
                    item.Episode?.Season ?? 1,
                    number,
                    (item.Episode?.Season ?? 1) * 100_000d + number,
                    item.Item.Title,
                    item.Url,
                    TimeSpan.Zero,
                    [])
                {
                    SourceId = 0,
                };
            }).ToList();
            return new LibrarySeries(
                ++seriesId,
                StableId(group.Key),
                seriesTitle,
                null,
                string.Empty,
                null,
                null,
                [],
                null,
                episodes,
                []);
        }).ToList();

        return new LibraryCatalog(1, feedUri.AbsoluteUri, series);
    }

    private static string? SelectMediaUrl(RssFeedItem item)
    {
        foreach (var candidate in new[] { item.EnclosureUrl, item.Link })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)) return uri.AbsoluteUri;
        }
        return null;
    }

    private static EpisodeInfo? ParseEpisode(string title)
    {
        var match = EpisodeSuffix.Match(title.Trim());
        if (!match.Success || !int.TryParse(match.Groups["episode"].Value, out var number)) return null;
        var rawSeries = GroupPrefix.Replace(match.Groups["series"].Value.Trim(), string.Empty).Trim();
        if (rawSeries.Length == 0) return null;
        var season = ParseSeason(rawSeries);
        var cleanSeries = SeasonSuffix.Replace(rawSeries, string.Empty).Trim();
        return new EpisodeInfo(cleanSeries.Length == 0 ? rawSeries : cleanSeries, season, number);
    }

    private static int ParseSeason(string title)
    {
        var raw = SeasonEnglish.Match(title).Groups[1].Value;
        if (raw.Length == 0) raw = SeasonTagged.Match(title).Groups[1].Value;
        if (raw.Length == 0) raw = SeasonChinese.Match(title).Groups[1].Value;
        if (int.TryParse(raw, out var season)) return Math.Max(1, season);
        var total = 0;
        var current = 0;
        foreach (var character in raw)
        {
            if (character == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
            }
            else if (character == '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
            }
            else
            {
                var digit = "一二三四五六七八九".IndexOf(character);
                if (digit < 0) return 1;
                current = current * 10 + digit + 1;
            }
        }
        return Math.Max(1, total + current);
    }

    private static Uri ValidateFeedUri(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(AppStrings.Get("rss.cleanUrl"), nameof(feedUrl));
        return uri;
    }

    private static string ProgressKey(string feedUrl, string itemId)
    {
        var value = Encoding.UTF8.GetBytes($"{feedUrl}\n{itemId}");
        return $"rss:{Convert.ToHexString(SHA256.HashData(value))[..32].ToLowerInvariant()}";
    }

    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record EpisodeInfo(string SeriesTitle, int Season, int Number);
    private sealed record ParsedItem(RssFeedItem Item, string Url, EpisodeInfo? Episode);
}
