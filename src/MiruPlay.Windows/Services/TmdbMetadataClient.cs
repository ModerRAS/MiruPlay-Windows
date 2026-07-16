using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MiruPlay.Windows.Services;

public sealed record TmdbSearchCandidate(
    int Id,
    string Title,
    string? OriginalTitle,
    string? Summary,
    string? FirstAirDate,
    string? PosterUrl,
    string? BackdropUrl)
{
    public string Provider { get; } = "TMDB";
    public string DetailUrl => $"https://www.themoviedb.org/tv/{Id}";
}

public sealed class TmdbMetadataClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public TmdbMetadataClient(HttpMessageHandler? handler = null, Uri? baseAddress = null)
    {
        _httpClient = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = baseAddress ?? new Uri("https://api.themoviedb.org/3/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "ModerRAS/MiruPlay-Windows/0.1 (https://github.com/ModerRAS/MiruPlay-Windows)");
    }

    public async Task<IReadOnlyList<TmdbSearchCandidate>> SearchAsync(
        string query,
        string accessToken,
        int? year = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var keyword = query.Trim();
        var token = accessToken.Trim();
        if (keyword.Length == 0) throw new ArgumentException("TMDB 搜索词不能为空。", nameof(query));
        if (keyword.Length > 100) throw new ArgumentException("TMDB 搜索词不能超过 100 个字符。", nameof(query));
        if (token.Length == 0) throw new InvalidOperationException("尚未配置 TMDB Read Access Token。");
        if (year is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(year), "首播年份无效。");
        var boundedLimit = Math.Clamp(limit, 1, 20);
        var url = $"search/tv?query={Uri.EscapeDataString(keyword)}&language=zh-CN&include_adult=false&page=1";
        if (year is not null) url += $"&first_air_date_year={year.Value}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"TMDB 搜索失败 (HTTP {(int)response.StatusCode})。",
                null,
                response.StatusCode);
        }
        var payload = await response.Content.ReadFromJsonAsync<TmdbSearchResponse>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("TMDB 搜索返回了空响应。");
        return payload.Results
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new TmdbSearchCandidate(
                item.Id,
                item.Name.Trim(),
                Clean(item.OriginalName),
                Clean(item.Overview, 2_000),
                Clean(item.FirstAirDate),
                ImageUrl("w500", item.PosterPath),
                ImageUrl("w1280", item.BackdropPath)))
            .Take(boundedLimit)
            .ToList();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string? Clean(string? value, int maximumLength = 300)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string? ImageUrl(string size, string? imagePath) =>
        string.IsNullOrWhiteSpace(imagePath) || !imagePath.StartsWith('/')
            ? null
            : $"https://image.tmdb.org/t/p/{size}{imagePath}";

    private sealed record TmdbSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbTvShow> Results);

    private sealed record TmdbTvShow(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath);
}
