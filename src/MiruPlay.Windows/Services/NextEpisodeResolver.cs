using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public static class NextEpisodeResolver
{
    public static LibraryEpisode? NextAfter(IReadOnlyList<LibraryEpisode> episodes, string currentProgressKey) =>
        Adjacent(episodes, currentProgressKey, 1);

    public static LibraryEpisode? PreviousBefore(IReadOnlyList<LibraryEpisode> episodes, string currentProgressKey) =>
        Adjacent(episodes, currentProgressKey, -1);

    public static IReadOnlyList<MpvPlaybackQueueEntry> BuildVersionQueue(IReadOnlyList<LibraryEpisode> episodes) =>
        episodes
            .GroupBy(episode => (episode.Season, episode.Number))
            .OrderBy(group => group.Key.Season)
            .ThenBy(group => group.Key.Number)
            .Select(group => new MpvPlaybackQueueEntry(
                group.Key.Season,
                group.Key.Number,
                group.OrderBy(episode => episode.MediaPath, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();

    private static LibraryEpisode? Adjacent(
        IReadOnlyList<LibraryEpisode> episodes,
        string currentProgressKey,
        int offset)
    {
        var queue = BuildVersionQueue(episodes);
        var index = queue
            .Select((entry, entryIndex) => (entry, entryIndex))
            .FirstOrDefault(item => item.entry.Versions.Any(version => version.ProgressKey == currentProgressKey))
            .entryIndex;
        if (queue.Count == 0 || !queue[index].Versions.Any(version => version.ProgressKey == currentProgressKey)) return null;
        var adjacent = index + offset;
        return adjacent >= 0 && adjacent < queue.Count ? queue[adjacent].DefaultVersion : null;
    }
}
