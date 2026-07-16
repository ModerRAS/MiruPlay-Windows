using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record DownloadedTorrent(ReadOnlyMemory<byte> Content, string FileName);

public sealed class TorrentFileDownloader
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly HttpMessageHandler? _handler;

    public TorrentFileDownloader(HttpMessageHandler? handler = null) => _handler = handler;

    public async Task<DownloadedTorrent> DownloadAsync(
        string url,
        string title,
        string keyPrefix,
        bool proxyEnabled,
        string proxyHost,
        int proxyPort,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("torrent 地址必须是无嵌入凭据的 HTTP(S) URL。", nameof(url));
        using var ownedHandler = _handler is null ? CreateHandler(proxyEnabled, proxyHost, proxyPort) : null;
        using var client = new HttpClient(_handler ?? ownedHandler!, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(finalUri.UserInfo))
            throw new InvalidDataException("torrent 重定向目标必须是无嵌入凭据的 HTTP(S) URL。");
        if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidDataException("torrent 文件超过 16 MiB。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxBytes) throw new InvalidDataException("torrent 文件超过 16 MiB。");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new InvalidDataException("torrent 文件为空。");
        return new DownloadedTorrent(output.ToArray(), TorrentFileName(title, finalUri, keyPrefix));
    }

    internal static string TorrentFileName(string title, Uri url, string keyPrefix)
    {
        var candidate = title.Trim().EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? title.Trim()
            : Uri.UnescapeDataString(url.Segments.LastOrDefault()?.Trim('/') ?? "rss-item.torrent");
        if (!candidate.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) candidate += ".torrent";
        candidate = Regex.Replace(candidate, "[\\\\/:*?\"<>|]", "_");
        candidate = Regex.Replace(candidate, "\\s+", " ").Trim();
        if (candidate.Length == 0) candidate = "rss-item.torrent";
        candidate = candidate[..Math.Min(candidate.Length, 180)];
        var prefix = new string(keyPrefix.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(12).ToArray());
        return prefix.Length == 0 ? candidate : $"{prefix}-{candidate}";
    }

    private static HttpClientHandler CreateHandler(bool enabled, string host, int port)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        if (enabled) handler.Proxy = new WebProxy(host.Trim(), port);
        return handler;
    }
}

public static class TorrentMagnetParser
{
    public static string Parse(ReadOnlySpan<byte> content)
    {
        var parser = new BencodeParser(content.ToArray());
        var root = parser.Parse() as BDictionary ?? throw new InvalidDataException("torrent 根节点不是字典。");
        var info = root.Get("info") as BDictionary ?? throw new InvalidDataException("torrent 缺少 info 字典。");
        var query = new List<string> { $"xt=urn:btih:{RssSubmissionPlanner.Sha1Hex(content[info.Start..info.End])}" };
        var name = (info.Get("name.utf-8") ?? info.Get("name"))?.AsString();
        if (!string.IsNullOrWhiteSpace(name)) query.Add($"dn={Uri.EscapeDataString(name[..Math.Min(name.Length, 1024)])}");
        var trackers = new HashSet<string>(StringComparer.Ordinal);
        root.Get("announce")?.CollectStrings(trackers);
        root.Get("announce-list")?.CollectStrings(trackers);
        query.AddRange(trackers.Where(value => value.Length is > 0 and <= 2048).Take(64).Select(value => $"tr={Uri.EscapeDataString(value)}"));
        return $"magnet:?{string.Join('&', query)}";
    }

    private abstract record BValue(int Start, int End)
    {
        public virtual string? AsString() => null;
        public virtual void CollectStrings(HashSet<string> values) { }
    }

    private sealed record BBytes(byte[] Value, int ValueStart, int ValueEnd) : BValue(ValueStart, ValueEnd)
    {
        public override string AsString() => Encoding.UTF8.GetString(Value);
        public override void CollectStrings(HashSet<string> values) => values.Add(AsString());
    }

    private sealed record BInteger(long Value, int ValueStart, int ValueEnd) : BValue(ValueStart, ValueEnd);

    private sealed record BList(IReadOnlyList<BValue> Values, int ValueStart, int ValueEnd) : BValue(ValueStart, ValueEnd)
    {
        public override void CollectStrings(HashSet<string> values)
        {
            foreach (var value in Values) value.CollectStrings(values);
        }
    }

    private sealed record BDictionary(IReadOnlyList<KeyValuePair<string, BValue>> Values, int ValueStart, int ValueEnd) : BValue(ValueStart, ValueEnd)
    {
        public BValue? Get(string key) => Values.FirstOrDefault(value => value.Key == key).Value;
        public override void CollectStrings(HashSet<string> values)
        {
            foreach (var value in Values) value.Value.CollectStrings(values);
        }
    }

    private sealed class BencodeParser(byte[] content)
    {
        private int _index;
        private int _nodes;

        public BValue Parse()
        {
            var value = ParseValue(0);
            if (_index != content.Length) throw new InvalidDataException($"torrent 在字节 {_index} 后包含多余数据。");
            return value;
        }

        private BValue ParseValue(int depth)
        {
            if (depth > 128 || ++_nodes > 100_000) throw new InvalidDataException("torrent bencode 结构过于复杂。");
            if (_index >= content.Length) throw new InvalidDataException("torrent bencode 意外结束。");
            return content[_index] switch
            {
                (byte)'i' => ParseInteger(),
                (byte)'l' => ParseList(depth),
                (byte)'d' => ParseDictionary(depth),
                >= (byte)'0' and <= (byte)'9' => ParseBytes(),
                _ => throw new InvalidDataException($"torrent bencode 类型无效：{_index}。"),
            };
        }

        private BInteger ParseInteger()
        {
            var start = _index++;
            var valueStart = _index;
            while (_index < content.Length && content[_index] != (byte)'e') _index++;
            if (_index >= content.Length || !long.TryParse(Encoding.ASCII.GetString(content, valueStart, _index - valueStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("torrent bencode 整数无效。");
            return new BInteger(value, start, ++_index);
        }

        private BList ParseList(int depth)
        {
            var start = _index++;
            var values = new List<BValue>();
            while (_index < content.Length && content[_index] != (byte)'e') values.Add(ParseValue(depth + 1));
            if (_index >= content.Length) throw new InvalidDataException("torrent bencode 列表未结束。");
            return new BList(values, start, ++_index);
        }

        private BDictionary ParseDictionary(int depth)
        {
            var start = _index++;
            var values = new List<KeyValuePair<string, BValue>>();
            while (_index < content.Length && content[_index] != (byte)'e')
            {
                var key = ParseBytes().AsString();
                values.Add(new KeyValuePair<string, BValue>(key, ParseValue(depth + 1)));
            }
            if (_index >= content.Length) throw new InvalidDataException("torrent bencode 字典未结束。");
            return new BDictionary(values, start, ++_index);
        }

        private BBytes ParseBytes()
        {
            var start = _index;
            var separator = Array.IndexOf(content, (byte)':', _index);
            if (separator < 0 || separator - _index is < 1 or > 10 ||
                !int.TryParse(Encoding.ASCII.GetString(content, _index, separator - _index), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                throw new InvalidDataException("torrent bencode 字符串长度无效。");
            _index = separator + 1;
            if (length > content.Length - _index) throw new InvalidDataException("torrent bencode 字符串越界。");
            var value = content.AsSpan(_index, length).ToArray();
            _index += length;
            return new BBytes(value, start, _index);
        }
    }
}

public sealed class TorrentSubmissionPreparer(
    CloudDriveGrpcClient cloudDrive,
    TorrentFileDownloader? downloader = null)
{
    private readonly TorrentFileDownloader _downloader = downloader ?? new TorrentFileDownloader();

    public async Task<string> PrepareAsync(
        CloudDriveAutomationConfig config,
        CloudDriveTokenInfo tokenInfo,
        string token,
        RssSubmissionDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (decision.SubmissionUrl is null || decision.ItemKey is null) throw new ArgumentException("RSS torrent 条目缺少提交信息。", nameof(decision));
        if (!tokenInfo.AllowList || !tokenInfo.AllowCreateFolder || !tokenInfo.AllowCreateFile || !tokenInfo.AllowWrite)
            throw new InvalidOperationException("CloudDrive2 API Token 缺少 torrent staging 所需的目录、创建或写入权限。");
        var downloaded = await _downloader.DownloadAsync(
            decision.SubmissionUrl,
            decision.Item.Title,
            RssSubmissionPlanner.Sha1Hex(decision.ItemKey)[..12],
            config.RssProxyEnabled,
            config.RssProxyHost,
            config.RssProxyPort,
            cancellationToken).ConfigureAwait(false);
        var magnet = TorrentMagnetParser.Parse(downloaded.Content.Span);
        var stagingPath = $"{config.InboxPath.TrimEnd('/')}/.miruplay-torrents";
        var entries = await cloudDrive.ListFolderAsync(config.EndpointUrl, token, config.InboxPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!entries.Any(entry => entry.IsDirectory && entry.Name == ".miruplay-torrents"))
            await cloudDrive.CreateFolderAsync(config.EndpointUrl, token, config.InboxPath, ".miruplay-torrents", cancellationToken).ConfigureAwait(false);
        await cloudDrive.UploadFileAsync(config.EndpointUrl, token, downloaded.Content, stagingPath, downloaded.FileName, cancellationToken).ConfigureAwait(false);
        return magnet;
    }
}
