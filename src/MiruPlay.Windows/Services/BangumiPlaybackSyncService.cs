using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public enum BangumiPlaybackSyncResult
{
    NotCompleted,
    MissingEpisodeId,
    MissingToken,
    Updated,
}

public sealed class BangumiPlaybackSyncService
{
    private readonly MetadataTokenStore _tokens;
    private readonly Func<int, int, string, CancellationToken, Task> _updateEpisode;

    public BangumiPlaybackSyncService(
        MetadataTokenStore tokens,
        Func<int, int, string, CancellationToken, Task> updateEpisode)
    {
        _tokens = tokens;
        _updateEpisode = updateEpisode;
    }

    public async Task<BangumiPlaybackSyncResult> MarkCompletedAsync(
        LibraryEpisode episode,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        if (!completed) return BangumiPlaybackSyncResult.NotCompleted;
        var episodeId = episode.ExternalId("Bangumi")?.NumericValue;
        if (episodeId is null) return BangumiPlaybackSyncResult.MissingEpisodeId;
        var token = _tokens.Load().Bangumi;
        if (string.IsNullOrEmpty(token)) return BangumiPlaybackSyncResult.MissingToken;
        await _updateEpisode(episodeId.Value, 2, token, cancellationToken).ConfigureAwait(false);
        return BangumiPlaybackSyncResult.Updated;
    }
}
