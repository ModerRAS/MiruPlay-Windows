using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MiruPlay.Windows.Services;

public sealed record BangumiSearchCandidate(
    int Id,
    string Title,
    string? TitleCn,
    string? Summary,
    string? AirDate,
    string? PosterUrl)
{
    public string Provider { get; } = "Bangumi";
    public string DetailUrl => $"https://bgm.tv/subject/{Id}";
}

public sealed record BangumiUser(int Id, string Username, string? Nickname);

public sealed record BangumiEpisodeCollectionState(
    [property: JsonPropertyName("episode_id")] int EpisodeId,
    [property: JsonPropertyName("episode_number")] int EpisodeNumber,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("updated_at")] long UpdatedAt);

public sealed record BangumiSubjectCollectionState(
    int SubjectId,
    int Type,
    int Rate,
    int EpisodeStatus,
    string? UpdatedAt);

public sealed class BangumiMetadataClient : IDisposable
{
    private static readonly Uri DefaultBaseAddress = new("https://api.bgm.tv/v0/");
    private readonly HttpClient _httpClient;

    public BangumiMetadataClient(HttpMessageHandler? handler = null, Uri? baseAddress = null)
    {
        _httpClient = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = baseAddress ?? DefaultBaseAddress,
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "ModerRAS/MiruPlay-Windows/0.1 (https://github.com/ModerRAS/MiruPlay-Windows)");
    }

    public async Task<IReadOnlyList<BangumiSearchCandidate>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var keyword = query.Trim();
        if (keyword.Length == 0) throw new ArgumentException("Bangumi 搜索词不能为空。", nameof(query));
        if (keyword.Length > 100) throw new ArgumentException("Bangumi 搜索词不能超过 100 个字符。", nameof(query));
        var boundedLimit = Math.Clamp(limit, 1, 20);
        using var response = await _httpClient.PostAsJsonAsync(
            $"search/subjects?limit={boundedLimit}",
            new BangumiSearchRequest(keyword, new BangumiSearchFilter([2])),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bangumi 搜索失败 (HTTP {(int)response.StatusCode})。",
                null,
                response.StatusCode);
        }
        var payload = await response.Content.ReadFromJsonAsync<BangumiSearchResponse>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Bangumi 搜索返回了空响应。");
        return payload.Data
            .Where(item => item.SubjectType == 2 && item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new BangumiSearchCandidate(
                item.Id,
                item.Name.Trim(),
                Clean(item.NameCn),
                Clean(item.Summary, 2_000),
                Clean(item.Date),
                BestPoster(item.Images)))
            .Take(boundedLimit)
            .ToList();
    }

    public async Task<BangumiUser> GetCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var token = accessToken.Trim();
        if (token.Length == 0) throw new InvalidOperationException("尚未配置 Bangumi Access Token。");
        using var request = new HttpRequestMessage(HttpMethod.Get, "me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bangumi 身份验证失败 (HTTP {(int)response.StatusCode})。",
                null,
                response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<BangumiUser>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Bangumi 身份验证返回了空响应。");
    }

    public async Task<BangumiSubjectCollectionState?> GetSubjectCollectionAsync(
        int subjectId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subjectId);
        var token = accessToken.Trim();
        if (token.Length == 0) throw new InvalidOperationException("尚未配置 Bangumi Access Token。");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"users/-/collections/{subjectId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bangumi 收藏读取失败 (HTTP {(int)response.StatusCode})。",
                null,
                response.StatusCode);
        }
        var value = await response.Content.ReadFromJsonAsync<BangumiSubjectCollectionResponse>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Bangumi 收藏返回了空响应。");
        return new BangumiSubjectCollectionState(value.SubjectId, value.Type, value.Rate, value.EpisodeStatus, value.UpdatedAt);
    }

    public async Task UpsertSubjectCollectionAsync(
        int subjectId,
        int type,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subjectId);
        if (type is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(type));
        var token = accessToken.Trim();
        if (token.Length == 0) throw new InvalidOperationException("尚未配置 Bangumi Access Token。");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"users/-/collections/{subjectId}")
        {
            Content = JsonContent.Create(new { type })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bangumi 收藏更新失败 (HTTP {(int)response.StatusCode})。",
                null,
                response.StatusCode);
        }
    }

    public async Task UpdateEpisodeCollectionAsync(
        int episodeId,
        int type,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episodeId);
        if (type is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(type));
        var token = accessToken.Trim();
        if (token.Length == 0) throw new InvalidOperationException("尚未配置 Bangumi Access Token。");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"users/-/collections/-/episodes/{episodeId}")
        {
            Content = JsonContent.Create(new { type })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Bangumi 分集状态更新失败 (HTTP {(int)response.StatusCode})。", null, response.StatusCode);
    }

    public async Task<IReadOnlyList<BangumiEpisodeCollectionState>> GetEpisodeCollectionsAsync(
        int subjectId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subjectId);
        var token = accessToken.Trim();
        if (token.Length == 0) throw new InvalidOperationException("尚未配置 Bangumi Access Token。");
        const int pageSize = 1_000;
        var values = new List<BangumiEpisodeCollectionState>();
        var offset = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"users/-/collections/{subjectId}/episodes?episode_type=0&limit={pageSize}&offset={offset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Bangumi 分集状态读取失败 (HTTP {(int)response.StatusCode})。", null, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<BangumiEpisodeCollectionPage>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Bangumi 分集状态返回了空响应。");
            values.AddRange(page.Data);
            offset += page.Data.Count;
            if (page.Data.Count == 0 || offset >= page.Total) break;
        }
        return values;
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

    private static string? BestPoster(BangumiImages? images)
    {
        var value = images?.Large ?? images?.Common ?? images?.Grid;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? uri.AbsoluteUri
            : null;
    }

    private sealed record BangumiSearchRequest(
        [property: JsonPropertyName("keyword")] string Keyword,
        [property: JsonPropertyName("filter")] BangumiSearchFilter Filter);

    private sealed record BangumiSearchFilter(
        [property: JsonPropertyName("type")] int[] SubjectType);

    private sealed record BangumiSearchResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<BangumiSubject> Data);

    private sealed record BangumiSubject(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("type")] int SubjectType,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("name_cn")] string? NameCn,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("images")] BangumiImages? Images);

    private sealed record BangumiEpisodeCollectionPage(
        [property: JsonPropertyName("data")] List<BangumiEpisodeCollectionState> Data,
        [property: JsonPropertyName("total")] int Total);

    private sealed record BangumiSubjectCollectionResponse(
        [property: JsonPropertyName("subject_id")] int SubjectId,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("rate")] int Rate,
        [property: JsonPropertyName("ep_status")] int EpisodeStatus,
        [property: JsonPropertyName("updated_at")] string? UpdatedAt);

    private sealed record BangumiImages(
        [property: JsonPropertyName("large")] string? Large,
        [property: JsonPropertyName("common")] string? Common,
        [property: JsonPropertyName("grid")] string? Grid);
}
