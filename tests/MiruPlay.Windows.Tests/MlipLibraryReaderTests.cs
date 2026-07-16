using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MlipLibraryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-windows-{Guid.NewGuid():N}");

    public MlipLibraryReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LoadReadsV3SeriesEpisodesSubtitlesAndExtras()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Frieren", "Season 1"));
        File.WriteAllText(Path.Combine(_root, "Frieren", "poster.jpg"), "poster");
        File.WriteAllText(Path.Combine(_root, "Frieren", "Season 1", "01.mkv"), "video");
        File.WriteAllText(Path.Combine(_root, "Frieren", "Season 1", "01.zh-CN.srt"), "subtitle");
        File.WriteAllText(Path.Combine(_root, "Frieren", "Special.mkv"), "extra");
        CreateDatabase();

        var catalog = MlipLibraryReader.Load(_root);

        var series = Assert.Single(catalog.Series);
        Assert.Equal(3, catalog.SchemaVersion);
        Assert.Equal("葬送的芙莉莲", series.Title);
        Assert.Equal("2023-09-29", series.AirDate);
        Assert.Equal(["冒险"], series.Genres);
        Assert.EndsWith(Path.Combine("Frieren", "poster.jpg"), series.PosterPath);
        Assert.Equal(["Bangumi", "TMDB", "AniDB"], series.ExternalIds.Select(item => item.Provider));
        Assert.Equal("https://bgm.tv/subject/431767", series.ExternalId("Bangumi")?.Link?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(209867, series.ExternalId("TMDB")?.NumericValue);
        var episode = Assert.Single(series.Episodes);
        Assert.Equal(1, episode.Number);
        Assert.EndsWith(Path.Combine("Frieren", "Season 1", "01.mkv"), episode.MediaPath);
        Assert.Single(episode.SubtitlePaths);
        Assert.Equal(12345, episode.ExternalId("Bangumi")?.NumericValue);
        Assert.Single(series.Extras);
    }

    [Fact]
    public void LoadSkipsFractionalEpisodeNumbers()
    {
        CreateDatabase();
        ExecuteSql("""
            INSERT INTO episode VALUES (2, 'episode-2', 1, 1, 1.5, 1.5, 'Part', NULL, 100);
            INSERT INTO media_file VALUES (2, 2, '/Frieren/Season 1/01.5.mkv', 5, 1);
            """);

        var series = Assert.Single(MlipLibraryReader.Load(_root).Series);

        Assert.Single(series.Episodes);
    }

    [Fact]
    public void ResolveRemoteEncodesSegmentsAndRejectsEscapes()
    {
        Assert.Equal(
            "https://example.com/dav/Anime/%E7%AC%AC%201%E9%9B%86.mkv",
            MlipPath.ResolveRemote("https://example.com/dav", "/Anime/第 1集.mkv"));
        Assert.Throws<InvalidDataException>(() => MlipPath.ResolveRemote("https://example.com/dav", "../outside.mkv"));
        Assert.Throws<InvalidDataException>(() => MlipPath.ResolveRemote("https://example.com/dav", "https://evil.invalid/video.mkv"));
    }

    [Fact]
    public void LoadRejectsUnknownExtraKind()
    {
        CreateDatabase();
        ExecuteSql("UPDATE media_extra SET extra_kind = 99");

        var error = Assert.Throws<InvalidDataException>(() => MlipLibraryReader.Load(_root));

        Assert.Contains("extra_kind", error.Message);
    }

    [Fact]
    public void LoadRejectsUnknownExternalIdProvider()
    {
        CreateDatabase();
        ExecuteSql("UPDATE series_external_id SET provider = 99 WHERE provider = 3");

        var error = Assert.Throws<InvalidDataException>(() => MlipLibraryReader.Load(_root));

        Assert.Contains("external_id provider", error.Message);
    }

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("/Anime/../../outside.mkv")]
    [InlineData("https://example.com/video.mkv")]
    public void ResolveLocalRejectsUntrustedPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => MlipPath.ResolveLocal(_root, path));
    }

    [Fact]
    public void ResolveLocalRejectsReparsePointEscape()
    {
        var outside = $"{_root}-outside";
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(outside);
        try
        {
            using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe",
                $"/d /c mklink /J \"{link}\" \"{outside}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            Assert.NotNull(junction);
            junction.WaitForExit();
            Assert.Equal(0, junction.ExitCode);
            Assert.Throws<InvalidDataException>(() => MlipPath.ResolveLocal(_root, "linked/video.mkv"));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void ResolveLocalAcceptsProtocolRootedRelativePaths()
    {
        var result = MlipPath.ResolveLocal(_root, "/Anime/Season 1/01.mkv");
        Assert.Equal(Path.Combine(_root, "Anime", "Season 1", "01.mkv"), result);
    }

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 3;
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE series(id INTEGER PRIMARY KEY, uuid TEXT UNIQUE NOT NULL, title TEXT NOT NULL, original_title TEXT, sort_title TEXT, summary TEXT, year INTEGER, series_type INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE series_release_date(series_id INTEGER PRIMARY KEY, air_date TEXT NOT NULL);
            CREATE TABLE episode(id INTEGER PRIMARY KEY, uuid TEXT UNIQUE NOT NULL, series_id INTEGER NOT NULL, season INTEGER NOT NULL, episode REAL NOT NULL, sort_order REAL NOT NULL, title TEXT, summary TEXT, runtime INTEGER);
            CREATE TABLE media_file(id INTEGER PRIMARY KEY, episode_id INTEGER NOT NULL, path TEXT NOT NULL UNIQUE, size INTEGER, modified_time INTEGER);
            CREATE TABLE media_subtitle(id INTEGER PRIMARY KEY, media_file_id INTEGER NOT NULL, path TEXT NOT NULL, language TEXT, title TEXT, sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE media_extra(id INTEGER PRIMARY KEY, uuid TEXT UNIQUE NOT NULL, series_id INTEGER NOT NULL, extra_kind INTEGER NOT NULL, ordinal INTEGER NOT NULL, sort_order INTEGER NOT NULL, title TEXT NOT NULL, path TEXT NOT NULL UNIQUE, size INTEGER, modified_time INTEGER, runtime INTEGER);
            CREATE TABLE series_artwork(id INTEGER PRIMARY KEY, series_id INTEGER NOT NULL, artwork_kind INTEGER NOT NULL, path TEXT NOT NULL);
            CREATE TABLE episode_artwork(id INTEGER PRIMARY KEY, episode_id INTEGER NOT NULL, artwork_kind INTEGER NOT NULL, path TEXT NOT NULL);
            CREATE TABLE genre(id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
            CREATE TABLE series_genre(series_id INTEGER NOT NULL, genre_id INTEGER NOT NULL, PRIMARY KEY(series_id, genre_id));
            CREATE TABLE series_external_id(series_id INTEGER NOT NULL, provider INTEGER NOT NULL, value TEXT NOT NULL, PRIMARY KEY(series_id, provider, value));
            CREATE TABLE episode_external_id(episode_id INTEGER NOT NULL, provider INTEGER NOT NULL, value TEXT NOT NULL, PRIMARY KEY(episode_id, provider, value));
            CREATE TABLE capability(name TEXT PRIMARY KEY, enabled INTEGER NOT NULL);

            INSERT INTO meta VALUES ('protocol', 'MLIP'), ('schema', '3');
            INSERT INTO capability VALUES ('extra', 1), ('subtitle', 1);
            INSERT INTO series VALUES (1, 'series-1', '葬送的芙莉莲', '葬送のフリーレン', NULL, '勇者一行之后的旅程。', 2023, 1);
            INSERT INTO series_release_date VALUES (1, '2023-09-29');
            INSERT INTO genre VALUES (1, '冒险');
            INSERT INTO series_genre VALUES (1, 1);
            INSERT INTO series_artwork VALUES (1, 1, 1, '/Frieren/poster.jpg');
            INSERT INTO series_external_id VALUES (1, 1, '431767'), (1, 2, '209867'), (1, 3, '18597');
            INSERT INTO episode VALUES (1, 'episode-1', 1, 1, 1, 1, '冒险的终点', NULL, 1500);
            INSERT INTO media_file VALUES (1, 1, '/Frieren/Season 1/01.mkv', 5, 1);
            INSERT INTO media_subtitle VALUES (1, 1, '/Frieren/Season 1/01.zh-CN.srt', 'zh-CN', '简体中文', 0);
            INSERT INTO episode_external_id VALUES (1, 1, '12345');
            INSERT INTO media_extra VALUES (1, 'extra-1', 1, 1, 1, 1, '特典', '/Frieren/Special.mkv', 5, 1, 120);
            """;
        command.ExecuteNonQuery();
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
