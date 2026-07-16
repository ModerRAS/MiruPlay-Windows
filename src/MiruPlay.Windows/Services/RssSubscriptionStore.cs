using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record RssSubscriptionInfo(
    long Id,
    string Name,
    string Url,
    string? FilterRegex = null,
    bool Enabled = true,
    long LastCheckedAt = 0);

public sealed record RssSubscriptionRequest(
    long Id,
    string Name,
    string Url,
    string? FilterRegex = null,
    bool Enabled = true);

public sealed class RssSubscriptionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private readonly string _path;

    public RssSubscriptionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "rss-subscriptions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
    }

    public IReadOnlyList<RssSubscriptionInfo> List()
    {
        lock (_lock) return Load().OrderBy(item => item.Id).ToList();
    }

    public RssSubscriptionInfo Add(RssSubscriptionRequest request)
    {
        var validated = Validate(request);
        lock (_lock)
        {
            var values = Load();
            var id = values.Count == 0 ? 1 : checked(values.Max(item => item.Id) + 1);
            var value = new RssSubscriptionInfo(id, validated.Name, validated.Url, validated.FilterRegex, validated.Enabled);
            values.Add(value);
            Save(values);
            return value;
        }
    }

    public RssSubscriptionInfo Update(long id, RssSubscriptionRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var validated = Validate(request);
        lock (_lock)
        {
            var values = Load();
            var index = values.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("RSS 订阅不存在。");
            var value = values[index] with
            {
                Name = validated.Name,
                Url = validated.Url,
                FilterRegex = validated.FilterRegex,
                Enabled = validated.Enabled,
            };
            values[index] = value;
            Save(values);
            return value;
        }
    }

    public void MarkChecked(long id, long timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        lock (_lock)
        {
            var values = Load();
            var index = values.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("RSS 订阅不存在。");
            values[index] = values[index] with { LastCheckedAt = Math.Max(0, timestamp) };
            Save(values);
        }
    }

    public void Remove(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        lock (_lock)
        {
            var values = Load();
            if (values.RemoveAll(item => item.Id == id) == 0) throw new KeyNotFoundException("RSS 订阅不存在。");
            Save(values);
        }
    }

    private static RssSubscriptionRequest Validate(RssSubscriptionRequest request)
    {
        var name = request.Name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("RSS 名称长度必须为 1 到 100 个字符。", nameof(request));
        var url = request.Url.Trim();
        if (url.Length > 2_048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("RSS 地址必须是不含嵌入凭据的 HTTP(S) URL。", nameof(request));
        var filter = string.IsNullOrWhiteSpace(request.FilterRegex) ? null : request.FilterRegex.Trim();
        if (filter?.Length > 500) throw new ArgumentException("RSS 过滤正则不能超过 500 个字符。", nameof(request));
        if (filter is not null)
        {
            try
            {
                _ = new Regex(filter, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (RegexParseException error)
            {
                throw new ArgumentException("RSS 过滤正则无效。", nameof(request), error);
            }
        }
        return request with { Name = name, Url = uri.AbsoluteUri, FilterRegex = filter };
    }

    private List<RssSubscriptionInfo> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RssSubscriptionInfo>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("RSS 订阅存储损坏。", error);
        }
    }

    private void Save(List<RssSubscriptionInfo> values)
    {
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(values, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }
}
