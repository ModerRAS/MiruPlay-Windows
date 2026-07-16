using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiPlaybackSyncServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-bangumi-sync-{Guid.NewGuid():N}");

    [Fact]
    public async Task CompletedEpisodeWithTokenMarksBangumiEpisodeDone()
    {
        var tokens = new MetadataTokenStore(Path.Combine(_directory, "tokens.bin"));
        tokens.SaveBangumi("secret-token");
        (int Id, int Type, string Token)? update = null;
        var service = new BangumiPlaybackSyncService(tokens, (id, type, token, _) =>
        {
            update = (id, type, token);
            return Task.CompletedTask;
        });
        var episode = Episode() with
        {
            ExternalIds = [new ExternalMetadataId("Bangumi", "9876")],
        };

        var result = await service.MarkCompletedAsync(episode, completed: true);

        Assert.Equal(BangumiPlaybackSyncResult.Updated, result);
        Assert.Equal((9876, 2, "secret-token"), update);
    }

    [Fact]
    public async Task IncompleteOrUnmappedEpisodesDoNotCallBangumi()
    {
        var tokens = new MetadataTokenStore(Path.Combine(_directory, "tokens.bin"));
        var calls = 0;
        var service = new BangumiPlaybackSyncService(tokens, (_, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var mapped = Episode() with { ExternalIds = [new ExternalMetadataId("Bangumi", "9876")] };

        Assert.Equal(BangumiPlaybackSyncResult.NotCompleted, await service.MarkCompletedAsync(mapped, completed: false));
        Assert.Equal(BangumiPlaybackSyncResult.MissingEpisodeId, await service.MarkCompletedAsync(Episode(), completed: true));
        Assert.Equal(BangumiPlaybackSyncResult.MissingToken, await service.MarkCompletedAsync(mapped, completed: true));
        Assert.Equal(0, calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static LibraryEpisode Episode() => new(
        1,
        "episode-uuid",
        "progress-key",
        1,
        1,
        1,
        "Episode",
        "episode.mkv",
        TimeSpan.FromMinutes(24),
        []);
}
