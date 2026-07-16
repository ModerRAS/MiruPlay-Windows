using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MiruPlay.Windows.Services;

public sealed record RssFeedItem(string Title, string? GuidValue, string? Link, string? EnclosureUrl)
{
    public string? SubmissionUrl => RssSubmissionPlanner.SelectSubmissionUrl(Link, EnclosureUrl);
}

public enum RssSubmissionStatus
{
    WouldSubmit,
    SkippedFilter,
    MissingSubmission,
}

public sealed record RssSubmissionDecision(
    RssFeedItem Item,
    string? SubmissionUrl,
    string? ItemKey,
    RssSubmissionStatus Status);

public sealed class RssFeedClient
{
    private const int MaximumFeedBytes = 5 * 1024 * 1024;
    private readonly HttpMessageHandler? _handler;

    public RssFeedClient(HttpMessageHandler? handler = null) => _handler = handler;

    public async Task<IReadOnlyList<RssFeedItem>> FetchAsync(
        string url,
        bool proxyEnabled = false,
        string proxyHost = "",
        int proxyPort = 1080,
        CancellationToken cancellationToken = default)
    {
        var feedUri = ValidateFeedUri(url);
        using var ownedHandler = _handler is null ? CreateHandler(proxyEnabled, proxyHost, proxyPort) : null;
        using var client = new HttpClient(_handler ?? ownedHandler!, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(feedUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"RSS 拉取失败 (HTTP {(int)response.StatusCode})。", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaximumFeedBytes) throw new InvalidDataException("RSS 响应超过 5 MiB。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new LimitedReadStream(stream, MaximumFeedBytes);
        return Parse(limited);
    }

    internal static IReadOnlyList<RssFeedItem> Parse(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFeedBytes,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(xml, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var rssItems = document.Descendants().Where(element => element.Name.LocalName == "item").Take(5_000).ToList();
        var elements = rssItems.Count > 0
            ? rssItems
            : document.Descendants().Where(element => element.Name.LocalName == "entry").Take(5_000).ToList();
        return elements.Select(ParseItem).ToList();
    }

    private static RssFeedItem ParseItem(XElement element)
    {
        var atom = element.Name.LocalName == "entry";
        var title = ChildText(element, "title");
        if (title.Length == 0) title = "未命名条目";
        var linkElements = element.Elements().Where(child => child.Name.LocalName == "link").ToList();
        var link = atom
            ? linkElements.Select(child => Clean(child.Attribute("href")?.Value ?? child.Value, 4_096)).FirstOrDefault(value => value is not null)
            : Clean(ChildText(element, "link"), 4_096);
        var enclosure = element.Elements()
            .Where(child => child.Name.LocalName == "enclosure")
            .Select(child => Clean(child.Attribute("url")?.Value, 4_096))
            .FirstOrDefault(value => value is not null);
        return new RssFeedItem(
            Clean(title, 1_000) ?? "未命名条目",
            Clean(ChildText(element, atom ? "id" : "guid"), 4_096),
            link,
            enclosure);
    }

    private static string ChildText(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string? Clean(string? value, int maximumLength)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static Uri ValidateFeedUri(string url)
    {
        var value = url.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("RSS 地址必须是不含嵌入凭据的 HTTP(S) URL。", nameof(url));
        return uri;
    }

    private static HttpClientHandler CreateHandler(bool proxyEnabled, string proxyHost, int proxyPort)
    {
        if (!proxyEnabled) return new HttpClientHandler();
        var host = proxyHost.Trim();
        if (host.Length == 0) throw new ArgumentException("启用 RSS 代理时必须填写代理主机。", nameof(proxyHost));
        return new HttpClientHandler { Proxy = new WebProxy(host, Math.Clamp(proxyPort, 1, 65_535)), UseProxy = true };
    }

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Track(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Track(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        private int Track(int count)
        {
            _read += count;
            if (_read > maximumBytes) throw new InvalidDataException("RSS 响应超过 5 MiB。");
            return count;
        }
    }
}

public static class RssSubmissionPlanner
{
    public static IReadOnlyList<RssSubmissionDecision> Plan(IReadOnlyList<RssFeedItem> items, string? filterRegex)
    {
        Regex? filter = null;
        if (!string.IsNullOrWhiteSpace(filterRegex))
        {
            try
            {
                filter = new Regex(filterRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (RegexParseException error)
            {
                throw new ArgumentException("RSS 过滤正则无效。", nameof(filterRegex), error);
            }
        }
        return items.Select(item =>
        {
            var submissionUrl = item.SubmissionUrl;
            var matches = filter?.IsMatch(item.Title) ?? true;
            var status = !matches
                ? RssSubmissionStatus.SkippedFilter
                : string.IsNullOrWhiteSpace(submissionUrl)
                    ? RssSubmissionStatus.MissingSubmission
                    : RssSubmissionStatus.WouldSubmit;
            return new RssSubmissionDecision(
                item,
                submissionUrl,
                string.IsNullOrWhiteSpace(submissionUrl) ? null : StableItemKey(item, submissionUrl),
                status);
        }).ToList();
    }

    internal static string? SelectSubmissionUrl(string? link, string? enclosure)
    {
        var candidates = new[] { link, enclosure }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToList();
        return candidates.FirstOrDefault(IsOfflineCandidate) ?? candidates.FirstOrDefault();
    }

    internal static string StableItemKey(RssFeedItem item, string submissionUrl)
    {
        if (!string.IsNullOrWhiteSpace(item.GuidValue)) return item.GuidValue.Trim();
        return Sha1Hex($"{item.Title}|{submissionUrl}");
    }

    private static bool IsOfflineCandidate(string value) =>
        value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
        value.Split('?', '#')[0].EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    // Protocol-compatible SHA-1 identifier; this is not used for authentication or integrity.
    internal static string Sha1Hex(string value) => Sha1Hex(Encoding.UTF8.GetBytes(value));

    internal static string Sha1Hex(ReadOnlySpan<byte> input)
    {
        var paddedLength = checked(((input.Length + 9 + 63) / 64) * 64);
        var padded = new byte[paddedLength];
        input.CopyTo(padded);
        padded[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(paddedLength - 8), checked((ulong)input.Length * 8));
        uint h0 = 0x67452301;
        uint h1 = 0xEFCDAB89;
        uint h2 = 0x98BADCFE;
        uint h3 = 0x10325476;
        uint h4 = 0xC3D2E1F0;
        Span<uint> words = stackalloc uint[80];
        for (var offset = 0; offset < padded.Length; offset += 64)
        {
            for (var index = 0; index < 16; index++)
                words[index] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(offset + index * 4, 4));
            for (var index = 16; index < 80; index++)
                words[index] = BitOperations.RotateLeft(words[index - 3] ^ words[index - 8] ^ words[index - 14] ^ words[index - 16], 1);
            var a = h0;
            var b = h1;
            var c = h2;
            var d = h3;
            var e = h4;
            for (var index = 0; index < 80; index++)
            {
                var (function, constant) = index switch
                {
                    < 20 => ((b & c) | (~b & d), 0x5A827999u),
                    < 40 => (b ^ c ^ d, 0x6ED9EBA1u),
                    < 60 => ((b & c) | (b & d) | (c & d), 0x8F1BBCDCu),
                    _ => (b ^ c ^ d, 0xCA62C1D6u),
                };
                var temporary = unchecked(BitOperations.RotateLeft(a, 5) + function + e + constant + words[index]);
                e = d;
                d = c;
                c = BitOperations.RotateLeft(b, 30);
                b = a;
                a = temporary;
            }
            h0 = unchecked(h0 + a);
            h1 = unchecked(h1 + b);
            h2 = unchecked(h2 + c);
            h3 = unchecked(h3 + d);
            h4 = unchecked(h4 + e);
        }
        Span<byte> hash = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(hash, h0);
        BinaryPrimitives.WriteUInt32BigEndian(hash[4..], h1);
        BinaryPrimitives.WriteUInt32BigEndian(hash[8..], h2);
        BinaryPrimitives.WriteUInt32BigEndian(hash[12..], h3);
        BinaryPrimitives.WriteUInt32BigEndian(hash[16..], h4);
        return Convert.ToHexStringLower(hash);
    }
}
