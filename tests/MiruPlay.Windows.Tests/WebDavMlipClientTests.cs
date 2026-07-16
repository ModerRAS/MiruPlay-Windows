using System.Net;
using System.Net.Sockets;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class WebDavMlipClientTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"miruplay-webdav-{Guid.NewGuid():N}");

    [Fact]
    public async Task AuthenticatedArtworkIsCachedOnlyWithinSourceRoot()
    {
        var artwork = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rootUrl = $"http://127.0.0.1:{port}/dav";
        var expectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:p@ss"));
        var serverTask = ServeArtworkAsync(listener, artwork, expectedAuthorization);
        using var client = new WebDavMlipClient(cacheRoot: _cacheRoot);

        var first = await client.DownloadArtworkAsync(
            rootUrl,
            $"{rootUrl}/posters/one.png",
            new MediaSourceCredential("alice", "p@ss"));
        var second = await client.DownloadArtworkAsync(
            rootUrl,
            $"{rootUrl}/posters/one.png",
            new MediaSourceCredential("alice", "p@ss"));
        await serverTask;

        Assert.Equal(first, second);
        Assert.Equal(artwork, await File.ReadAllBytesAsync(first));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadArtworkAsync(
            rootUrl,
            $"http://127.0.0.1:{port}/other/poster.png",
            new MediaSourceCredential("alice", "p@ss")));

        client.DeleteCache(rootUrl);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(first))));
    }

    [Theory]
    [InlineData("https://alice:secret@example.test/dav")]
    [InlineData("https://example.test/dav?token=secret")]
    [InlineData("https://example.test/dav#fragment")]
    public void RootRejectsCredentialsQueryAndFragment(string rootUrl)
    {
        Assert.Throws<ArgumentException>(() => WebDavMlipClient.NormalizeRoot(rootUrl));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, true);
    }

    private static async Task ServeArtworkAsync(
        TcpListener listener,
        byte[] artwork,
        string expectedAuthorization)
    {
        using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        string? line;
        string? authorization = null;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                authorization = line["Authorization:".Length..].Trim();
            }
        }
        Assert.Equal("GET /dav/posters/one.png HTTP/1.1", requestLine);
        Assert.Equal(expectedAuthorization, authorization);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {artwork.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(artwork);
    }
}
