using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class DirectoryLibraryIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-directory-index-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public DirectoryLibraryIndexTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "index.db");
    }

    [Fact]
    public async Task CanceledScanLeavesPreviousRowsAndRejectsTraversal()
    {
        var index = new DirectoryLibraryIndex(_databasePath);
        await index.ScanAsync(
            7,
            _root,
            [new DirectoryFileEntry("Anime/01.mkv", 10, 1)]);

        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnFirstProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.ScanAsync(
            7,
            _root,
            [
                new DirectoryFileEntry("Anime/01.mkv", 11, 2),
                new DirectoryFileEntry("Anime/02.mkv", 12, 3),
            ],
            progress,
            cancellation.Token));

        Assert.Single(index.LoadDirectory(7, _root).Series.SelectMany(series => series.Episodes));
        await Assert.ThrowsAsync<InvalidDataException>(() => index.ScanAsync(
            7,
            _root,
            [new DirectoryFileEntry("../outside.mkv", 1, 1)]));
    }

    [Fact]
    public async Task ReconciliationPersistsOnlyChangesAndReportsDeletions()
    {
        var index = new DirectoryLibraryIndex(_databasePath);
        var first = await index.ScanAsync(
            3,
            _root,
            [
                new DirectoryFileEntry("Anime/01.mkv", 10, 1, ["Anime/01.ass"]),
                new DirectoryFileEntry("Anime/02.mkv", 20, 1),
            ]);
        var second = await index.ScanAsync(
            3,
            _root,
            [new DirectoryFileEntry("Anime/01.mkv", 10, 1, ["Anime/01.srt"])]);

        Assert.Equal(2, first.NewEpisodes);
        Assert.Equal(1, second.DeletedEpisodes);
        Assert.Equal(1, second.UpdatedEpisodes);
        var episode = Assert.Single(index.LoadDirectory(3, _root).Series.SelectMany(series => series.Episodes));
        Assert.Equal(Path.Combine(_root, "Anime", "01.srt"), Assert.Single(episode.SubtitlePaths));
    }

    [Fact]
    public async Task ScannerUsesSharedClassifierAndRejectsUnboundedEntryLists()
    {
        var index = new DirectoryLibraryIndex(_databasePath, new StaticClassifier());
        await index.ScanAsync(9, _root, [new DirectoryFileEntry("Anime/file.mkv", 1, 1)]);

        var series = Assert.Single(index.LoadDirectory(9, _root).Series);
        var episode = Assert.Single(series.Episodes);
        Assert.Equal("Injected title", series.Title);
        Assert.Equal(4, episode.Season);
        Assert.Equal(9, episode.Number);

        var oversized = Enumerable.Range(0, DirectoryLibraryIndex.MaximumEntries + 1)
            .Select(number => new DirectoryFileEntry($"Anime/{number}.mkv", 1, 1))
            .ToList();
        await Assert.ThrowsAsync<InvalidDataException>(() => index.ScanAsync(9, _root, oversized));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class CancelOnFirstProgress(CancellationTokenSource source) : IProgress<DirectoryScanProgress>
    {
        private int _called;
        public void Report(DirectoryScanProgress value)
        {
            if (Interlocked.Exchange(ref _called, 1) == 0) source.Cancel();
        }
    }

    private sealed class StaticClassifier : IAnimeVideoClassifier
    {
        public VideoClassification Classify(string path, string fileName, string? parentName = null) =>
            new("Injected title", 4, 9);
    }
}
