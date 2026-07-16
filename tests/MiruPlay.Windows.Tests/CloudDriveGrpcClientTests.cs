using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class CloudDriveGrpcClientTests
{
    [Fact]
    public async Task LoginAndTokenInfoNormalizeEndpointAndInputs()
    {
        Uri? loginEndpoint = null;
        string? loginUser = null;
        string? loginPassword = null;
        Uri? tokenEndpoint = null;
        string? verifiedToken = null;
        var client = new CloudDriveGrpcClient(
            (endpoint, username, password, _) =>
            {
                loginEndpoint = endpoint;
                loginUser = username;
                loginPassword = password;
                return Task.FromResult(new CloudDriveLoginResult("issued-token"));
            },
            (endpoint, token, _) =>
            {
                tokenEndpoint = endpoint;
                verifiedToken = token;
                return Task.FromResult(new CloudDriveTokenInfo("/Anime", "MiruPlay", true, true, false, false, true, true));
            });

        var login = await client.LoginAsync(" http://localhost:19798/ ", " user ", "password");
        var info = await client.GetApiTokenInfoAsync("http://localhost:19798", " api-token ");

        Assert.Equal("http://localhost:19798/", loginEndpoint?.AbsoluteUri);
        Assert.Equal("user", loginUser);
        Assert.Equal("password", loginPassword);
        Assert.Equal("issued-token", login.Token);
        Assert.Equal(loginEndpoint, tokenEndpoint);
        Assert.Equal("api-token", verifiedToken);
        Assert.True(info.AllowAddOfflineDownload);
        Assert.False(info.AllowCreateFile);
    }

    [Fact]
    public async Task FolderListingNormalizesPathAndRejectsTraversal()
    {
        Uri? usedEndpoint = null;
        string? usedToken = null;
        string? usedPath = null;
        var client = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/", "", true, false, false, false, false, false)),
            (endpoint, token, path, forceRefresh, _) =>
            {
                usedEndpoint = endpoint;
                usedToken = token;
                usedPath = path;
                Assert.False(forceRefresh);
                return Task.FromResult<IReadOnlyList<CloudDriveFileInfo>>([new("Anime", "/Anime", true, 0)]);
            });

        var files = await client.ListFolderAsync("http://localhost:19798", " api-token ", "\\Anime//Season 1/");

        Assert.Single(files);
        Assert.Equal("http://localhost:19798/", usedEndpoint?.AbsoluteUri);
        Assert.Equal("api-token", usedToken);
        Assert.Equal("/Anime/Season 1", usedPath);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListFolderAsync("http://localhost:19798", "token", "/Anime/../Secret"));
    }

    [Fact]
    public async Task OfflineSubmissionNormalizesAndBoundsInputs()
    {
        IReadOnlyList<string>? submittedUrls = null;
        string? submittedTarget = null;
        var client = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/", "", true, false, false, false, false, true)),
            addOfflineFiles: (_, token, urls, target, _) =>
            {
                Assert.Equal("api-token", token);
                submittedUrls = urls;
                submittedTarget = target;
                return Task.CompletedTask;
            });

        await client.AddOfflineFilesAsync(
            "http://localhost:19798",
            " api-token ",
            [" magnet:?xt=urn:btih:abc ", "https://example.test/file.torrent"],
            "\\Downloads//RSS/");

        Assert.Equal(["magnet:?xt=urn:btih:abc", "https://example.test/file.torrent"], submittedUrls);
        Assert.Equal("/Downloads/RSS", submittedTarget);
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddOfflineFilesAsync("http://localhost:19798", "token", ["file:///secret"], "/Downloads"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddOfflineFilesAsync("http://localhost:19798", "token", ["https://example.test/file"], "/"));
    }

    [Fact]
    public async Task FolderCreationAndUploadNormalizeInputs()
    {
        string? created = null;
        string? uploaded = null;
        var client = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/", "", true, true, true, true, false, false)),
            createFolder: (_, token, parent, name, _) =>
            {
                created = $"{token}|{parent}|{name}";
                return Task.CompletedTask;
            },
            uploadFile: (_, token, content, parent, name, _) =>
            {
                uploaded = $"{token}|{parent}|{name}|{content.Length}";
                return Task.FromResult($"{parent}/{name}");
            });

        await client.CreateFolderAsync("http://localhost:19798", " token ", "\\Anime//Downloads", ".stage");
        var path = await client.UploadFileAsync("http://localhost:19798", " token ", "abc"u8.ToArray(), "/Anime/Downloads/.stage", "show.torrent");

        Assert.Equal("token|/Anime/Downloads|.stage", created);
        Assert.Equal("token|/Anime/Downloads/.stage|show.torrent|3", uploaded);
        Assert.Equal("/Anime/Downloads/.stage/show.torrent", path);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadFileAsync("http://localhost:19798", "token", ReadOnlyMemory<byte>.Empty, "/Anime", "x.torrent"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateFolderAsync("http://localhost:19798", "token", "/Anime", "../bad"));
    }

    [Fact]
    public async Task MoveFilesNormalizesAndBoundsPaths()
    {
        IReadOnlyList<string>? moved = null;
        string? destination = null;
        var client = new CloudDriveGrpcClient(
            (_, _, _, _) => Task.FromResult(new CloudDriveLoginResult("token")),
            (_, _, _) => Task.FromResult(new CloudDriveTokenInfo("/", "", true, false, false, false, true, false)),
            moveFiles: (_, token, paths, target, _) =>
            {
                Assert.Equal("api-token", token);
                moved = paths;
                destination = target;
                return Task.CompletedTask;
            });

        await client.MoveFilesAsync("http://localhost:19798", " api-token ", ["\\Anime//Downloads/show.mkv"], "\\Anime//Library/Show");

        Assert.Equal(["/Anime/Downloads/show.mkv"], moved);
        Assert.Equal("/Anime/Library/Show", destination);
        await Assert.ThrowsAsync<ArgumentException>(() => client.MoveFilesAsync("http://localhost:19798", "token", ["/Anime/../Secret"], "/Anime/Library"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.MoveFilesAsync("http://localhost:19798", "token", ["/"], "/Anime/Library"));
    }

    [Theory]
    [InlineData("ftp://localhost:19798")]
    [InlineData("http://localhost:19798/api")]
    [InlineData("http://localhost:19798/?token=secret")]
    public async Task RejectsInvalidEndpointBeforeTransport(string endpoint)
    {
        var calls = 0;
        var client = new CloudDriveGrpcClient(
            (_, _, _, _) => { calls++; return Task.FromResult(new CloudDriveLoginResult("token")); },
            (_, _, _) => { calls++; return Task.FromResult(new CloudDriveTokenInfo("", "", false, false, false, false, false, false)); });

        await Assert.ThrowsAsync<ArgumentException>(() => client.LoginAsync(endpoint, "user", "password"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetApiTokenInfoAsync(endpoint, "token"));
        Assert.Equal(0, calls);
    }
}
