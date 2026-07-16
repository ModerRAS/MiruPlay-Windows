using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public static class NextEpisodeResolver
{
    public static LibraryEpisode? NextAfter(IReadOnlyList<LibraryEpisode> episodes, string currentProgressKey)
    {
        var ordered = episodes
            .OrderBy(episode => episode.Season)
            .ThenBy(episode => episode.Number)
            .ThenBy(episode => episode.MediaPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var index = ordered.FindIndex(episode => episode.ProgressKey == currentProgressKey);
        return index >= 0 && index + 1 < ordered.Count ? ordered[index + 1] : null;
    }
}
