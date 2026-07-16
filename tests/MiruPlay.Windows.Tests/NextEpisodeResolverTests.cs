using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class NextEpisodeResolverTests
{
    [Fact]
    public void NextAfterUsesSeasonEpisodeAndPathOrdering()
    {
        var episodes = new[]
        {
            Episode("s2e1", 2, 1, "S2/01.mkv"),
            Episode("s1e2b", 1, 2, "S1/02-b.mkv"),
            Episode("s1e1", 1, 1, "S1/01.mkv"),
            Episode("s1e2a", 1, 2, "S1/02-a.mkv"),
        };

        Assert.Equal("s1e2a", NextEpisodeResolver.NextAfter(episodes, "s1e1")?.ProgressKey);
        Assert.Equal("s1e2b", NextEpisodeResolver.NextAfter(episodes, "s1e2a")?.ProgressKey);
        Assert.Equal("s2e1", NextEpisodeResolver.NextAfter(episodes, "s1e2b")?.ProgressKey);
        Assert.Null(NextEpisodeResolver.NextAfter(episodes, "s2e1"));
    }

    private static LibraryEpisode Episode(string key, int season, double number, string path) => new(
        1,
        key,
        key,
        season,
        number,
        number,
        key,
        path,
        TimeSpan.Zero,
        []);
}
