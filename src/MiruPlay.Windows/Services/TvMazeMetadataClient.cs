using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record TvMazeSearchCandidate(
    int Id,
    string Title,
    string? OriginalTitle,
    string? Summary,
    string? FirstAirDate,
    string? PosterUrl,
    string? BackdropUrl)
{
    public string Provider { get; } = "TVMaze";
    public string DetailUrl => $"https://www.tvmaze.com/shows/{Id}";
}

public sealed partial class TvMazeMetadataClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public TvMazeMetadataClient(HttpMessageHandler? handler = null, Uri? baseAddress = null)
    {
        _httpClient = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = baseAddress ?? new Uri("https://api.tvmaze.com/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MiruPlay-Windows/1.0");
    }

    public async Task<IReadOnlyList<TvMazeSearchCandidate>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var keyword = query.Trim();
        if (keyword.Length is 0 or > 100) throw new ArgumentException("TVMaze 搜索词必须为 1 至 100 个字符。", nameof(query));
        using var response = await _httpClient.GetAsync($"search/shows?q={Uri.EscapeDataString(keyword)}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TVMaze 搜索失败 (HTTP {(int)response.StatusCode})。", null, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<SearchResult>>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("TVMaze 搜索返回了空响应。");
        return payload.Select(item => item.Show)
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Take(Math.Clamp(limit, 1, 20))
            .Select(item => new TvMazeSearchCandidate(
                item.Id,
                item.Name.Trim(),
                Clean(item.OriginalName),
                Clean(item.Summary),
                Clean(item.Premiered),
                Image(item.Image?.Medium),
                Image(item.Image?.Original)))
            .ToList();
    }

    public void Dispose() => _httpClient.Dispose();

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var plain = Whitespace().Replace(System.Net.WebUtility.HtmlDecode(HtmlTags().Replace(value, " ")), " ").Trim();
        return plain.Length <= 2_000 ? plain : plain[..2_000];
    }

    private static string? Image(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri.AbsoluteUri : null;

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("<[^>]{1,512}>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTags();

    private sealed record SearchResult([property: JsonPropertyName("show")] Show Show);
    private sealed record Show(
        int Id,
        string Name,
        [property: JsonPropertyName("name_original")] string? OriginalName,
        string? Summary,
        string? Premiered,
        ImageSet? Image);
    private sealed record ImageSet(string? Medium, string? Original);
}
