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
                new DirectoryFileEntry("Anime/01.mkv", 10, 1, ["Anime/01.ass"], ExternalAudioPaths: ["Anime/01.en.flac"]),
                new DirectoryFileEntry("Anime/02.mkv", 20, 1),
            ]);
        var second = await index.ScanAsync(
            3,
            _root,
            [new DirectoryFileEntry("Anime/01.mkv", 10, 1, ["Anime/01.srt"], ExternalAudioPaths: ["Anime/01.ja.mka"])]);

        Assert.Equal(2, first.NewEpisodes);
        Assert.Equal(1, second.DeletedEpisodes);
        Assert.Equal(1, second.UpdatedEpisodes);
        var episode = Assert.Single(index.LoadDirectory(3, _root).Series.SelectMany(series => series.Episodes));
        Assert.Equal(Path.Combine(_root, "Anime", "01.srt"), Assert.Single(episode.SubtitlePaths));
        Assert.Equal(Path.Combine(_root, "Anime", "01.ja.mka"), Assert.Single(episode.ExternalAudioPaths));
    }

    [Fact]
    public void ExistingVersionTwoIndexAddsAudioSidecarColumnWithoutLosingRows()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE directory_episode(
                    source_id INTEGER NOT NULL, episode_uuid TEXT NOT NULL, series_uuid TEXT NOT NULL,
                    series_title TEXT NOT NULL, season INTEGER NOT NULL, episode_number REAL NOT NULL,
                    relative_path TEXT NOT NULL, subtitle_paths_json TEXT NOT NULL DEFAULT '[]',
                    size_bytes INTEGER NOT NULL DEFAULT 0, modified_ms INTEGER NOT NULL DEFAULT 0,
                    episode_metadata_json TEXT NOT NULL DEFAULT 'null', series_metadata_json TEXT NOT NULL DEFAULT 'null',
                    PRIMARY KEY(source_id, relative_path));
                INSERT INTO directory_episode VALUES(1, 'episode', 'series', 'Legacy', 1, 1, 'Anime/01.mkv', '[]', 10, 20, 'null', 'null');
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        var catalog = new DirectoryLibraryIndex(_databasePath).LoadDirectory(1, _root);
        var series = Assert.Single(catalog.Series);
        var episode = Assert.Single(series.Episodes);

        Assert.Equal("Legacy", series.Title);
        Assert.Empty(episode.ExternalAudioPaths);
        using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        migrated.Open();
        using var version = migrated.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, (long)version.ExecuteScalar()!);
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

    [Fact]
    public async Task LocalNfoMetadataIsCachedReconciledAndDoesNotReplaceProgressIdentity()
    {
        var show = Path.Combine(_root, "Show");
        var season = Path.Combine(show, "Season 2");
        Directory.CreateDirectory(season);
        var media = Path.Combine(season, "Show.S02E03.mkv");
        File.WriteAllText(media, "video");
        var nfo = new NfoDocumentService(_root);
        nfo.WriteTvShow(show, new NfoTvShowMetadata(Title: "Matched show", OriginalTitle: "Original",
            Plot: "Plot", Genres: ["Drama"], Premiered: "2026-09-01", UniqueIds: [new("tmdb", "42")]));
        nfo.WriteEpisode(media, new NfoEpisodeMetadata(Title: "Episode title", Season: 2, Episode: 3,
            UniqueIds: [new("bangumi", "123")]));
        var index = new DirectoryLibraryIndex(_databasePath);
        await index.ScanLocalAsync(7, _root);
        var series = Assert.Single(index.LoadLocal(7, _root).Series);
        var episode = Assert.Single(series.Episodes);
        Assert.Equal("Matched show", series.Title);
        Assert.Equal("Original", series.OriginalTitle);
        Assert.Equal("Plot", series.Summary);
        Assert.Equal("2026-09-01", series.AirDate);
        Assert.Equal(2026, series.Year);
        Assert.Equal("42", series.ExternalId("TMDB")?.Value);
        Assert.Equal("Episode title", episode.Title);
        Assert.Equal(2, episode.Season);
        Assert.Equal(3, episode.Number);
        Assert.Equal("123", episode.ExternalId("Bangumi")?.Value);
        nfo.WriteEpisode(media, new NfoEpisodeMetadata(Title: "Updated", Season: 2, Episode: 3));
        Assert.Equal(1, (await index.ScanLocalAsync(7, _root)).UpdatedEpisodes);
        var updated = Assert.Single(new DirectoryLibraryIndex(_databasePath).LoadLocal(7, _root).Series.SelectMany(item => item.Episodes));
        Assert.Equal("Updated", updated.Title);
        Assert.Equal(episode.ProgressKey, updated.ProgressKey);
        Assert.Equal(0, (await index.ScanLocalAsync(7, _root)).UpdatedEpisodes);
        File.WriteAllText(nfo.EpisodePath(media), "<!DOCTYPE x [<!ENTITY ex SYSTEM 'file:///secret'>]><episodedetails><title>&ex;</title></episodedetails>");
        await Assert.ThrowsAsync<InvalidDataException>(() => index.ScanLocalAsync(7, _root));
        Assert.Equal("Updated", Assert.Single(index.LoadLocal(7, _root).Series.SelectMany(item => item.Episodes)).Title);
    }

    [Fact]
    public async Task PartialNfoKeepsFilenameSeasonAndEpisodeNumbers()
    {
        var index = new DirectoryLibraryIndex(_databasePath, new StaticClassifier());
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<episodedetails><title>Title only</title></episodedetails>"));
        var metadata = NfoDocumentService.ReadEpisode(stream);
        await index.ScanAsync(9, _root, [new DirectoryFileEntry("Anime/file.mkv", 1, 1, EpisodeMetadata: metadata)]);
        var episode = Assert.Single(index.LoadDirectory(9, _root).Series.SelectMany(item => item.Episodes));
        Assert.Equal(4, episode.Season);
        Assert.Equal(9, episode.Number);
        Assert.Equal("Title only", episode.Title);
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
