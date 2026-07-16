using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class CloudDriveLibraryOrganizerTests
{
    [Theory]
    [InlineData("[SubsPlease] Frieren - S02E03 [1080p].mkv", "Frieren", 2)]
    [InlineData("01.mkv", "My Show", 1)]
    [InlineData("Show Name - 12v2.mp4", "Show Name", 1)]
    public void FilenameClassifierMatchesAndroidOrganizationRules(string fileName, string show, int season)
    {
        var result = VideoFilenameInference.Classify(fileName, "My Show");

        Assert.Equal(show, result.ShowName);
        Assert.Equal(season, result.SeasonNumber);
    }

    [Fact]
    public async Task OrganizerFiltersScopeCreatesFoldersAndMovesVideos()
    {
        var created = new List<string>();
        var moved = new List<string>();
        var listings = new Dictionary<string, IReadOnlyList<CloudDriveFileInfo>>(StringComparer.Ordinal)
        {
            ["/Anime/Downloads"] =
            [
                new("Batch", "/Anime/Downloads/Batch", true, 0),
                new(".hidden", "/Anime/Downloads/.hidden", true, 0),
                new("escape.mkv", "/Secret/escape.mkv", false, 1),
            ],
            ["/Anime/Downloads/Batch"] =
            [
                new("Frieren.S02E03.mkv", "/Anime/Downloads/Batch/Frieren.S02E03.mkv", false, 10),
                new("notes.txt", "/Anime/Downloads/Batch/notes.txt", false, 1),
                new("cache.trickplay", "/Anime/Downloads/Batch/cache.trickplay", true, 0),
            ],
            ["/Anime/Library"] = [],
            ["/Anime/Library/Frieren"] = [],
        };
        var cloud = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/Anime", "", true, true, false, false, true, false)),
            (_, _, path, _, _) => Task.FromResult(listings.GetValueOrDefault(path, [])),
            createFolder: (_, _, parent, name, _) =>
            {
                created.Add($"{parent}/{name}");
                return Task.CompletedTask;
            },
            moveFiles: (_, _, paths, destination, _) =>
            {
                moved.Add($"{Assert.Single(paths)}->{destination}");
                return Task.CompletedTask;
            });
        var organizer = new CloudDriveLibraryOrganizer(cloud);

        var count = await organizer.OrganizeAsync(
            new CloudDriveAutomationConfig(
                "http://localhost:19798",
                InboxPath: "/Anime/Downloads",
                LibraryPath: "/Anime/Library",
                Enabled: true),
            new CloudDriveTokenInfo("/Anime", "", true, true, false, false, true, false),
            "token");

        Assert.Equal(1, count);
        Assert.Equal(["/Anime/Library/Frieren", "/Anime/Library/Frieren/Season 2"], created);
        Assert.Equal(["/Anime/Downloads/Batch/Frieren.S02E03.mkv->/Anime/Library/Frieren/Season 2"], moved);
    }

    [Fact]
    public async Task OrganizerRejectsLibraryInsideInboxBeforeTransport()
    {
        var calls = 0;
        var cloud = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/Anime", "", true, true, false, false, true, false)),
            (_, _, _, _, _) => { calls++; return Task.FromResult<IReadOnlyList<CloudDriveFileInfo>>([]); });
        var organizer = new CloudDriveLibraryOrganizer(cloud);

        await Assert.ThrowsAsync<InvalidOperationException>(() => organizer.OrganizeAsync(
            new CloudDriveAutomationConfig(
                "http://localhost:19798",
                InboxPath: "/Anime/Downloads",
                LibraryPath: "/Anime/Downloads/Library",
                Enabled: true),
            new CloudDriveTokenInfo("/Anime", "", true, true, false, false, true, false),
            "token"));
        Assert.Equal(0, calls);
    }
}
