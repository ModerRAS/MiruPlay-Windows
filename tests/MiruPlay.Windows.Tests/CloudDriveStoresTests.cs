using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class CloudDriveStoresTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-cloud-{Guid.NewGuid():N}");

    [Fact]
    public void ConfigNormalizesAndroidCompatibleBoundsAndPersistsEnumName()
    {
        var path = Path.Combine(_directory, "cloud.json");
        var store = new CloudDriveAutomationStore(path);

        var saved = store.Save(new CloudDriveAutomationConfig(
            " http://localhost:19798/ ",
            " user ",
            0,
            " /downloads ",
            " /library ",
            CloudDriveLibraryMode.SingleDirectory,
            3,
            false,
            RssProxyPort: 99_999));

        Assert.Equal("http://localhost:19798", saved.EndpointUrl);
        Assert.Equal("user", saved.Username);
        Assert.Null(saved.WebDavSourceId);
        Assert.Equal(15, saved.IntervalMinutes);
        Assert.Equal(65_535, saved.RssProxyPort);
        Assert.Equal(CloudDriveLibraryMode.SingleDirectory, new CloudDriveAutomationStore(path).Load().LibraryMode);
        Assert.Contains("SINGLE_DIRECTORY", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("proxy.example.test", 8080)]
    [InlineData("127.0.0.1", 1080)]
    public void EnabledProxyRequiresAnHttpHostAndValidPort(string host, int port)
    {
        var saved = new CloudDriveAutomationStore(Path.Combine(_directory, $"proxy-{Guid.NewGuid():N}.json"))
            .Save(new CloudDriveAutomationConfig(RssProxyEnabled: true, RssProxyHost: host, RssProxyPort: port));
        Assert.Equal(host, saved.RssProxyHost);
        Assert.Equal(port, saved.RssProxyPort);
    }

    [Theory]
    [InlineData("http://user:pass@proxy.example.test", 8080)]
    [InlineData("proxy.example.test/path", 8080)]
    [InlineData("proxy.example.test", 0)]
    [InlineData("proxy.example.test", 65_536)]
    public void EnabledProxyRejectsCredentialsPathsAndInvalidPorts(string host, int port)
    {
        var store = new CloudDriveAutomationStore(Path.Combine(_directory, $"invalid-proxy-{Guid.NewGuid():N}.json"));
        Assert.Throws<ArgumentException>(() => store.Save(new CloudDriveAutomationConfig(
            RssProxyEnabled: true, RssProxyHost: host, RssProxyPort: port)));
    }

    [Fact]
    public void OutboundProxyReadsCloudDriveSettingsDynamically()
    {
        var config = new CloudDriveAutomationConfig();
        using var handler = OutboundHttpProxy.CreateHandler(() => config);
        var proxy = handler.Proxy!;
        var destination = new Uri("https://metadata.example.test/search");
        Assert.True(proxy.IsBypassed(destination));
        config = config with { RssProxyEnabled = true, RssProxyHost = "proxy.example.test", RssProxyPort = 8080 };
        Assert.False(proxy.IsBypassed(destination));
        Assert.Equal("http://proxy.example.test:8080/", proxy.GetProxy(destination)!.AbsoluteUri);
        config = config with { RssProxyEnabled = false };
        Assert.True(proxy.IsBypassed(destination));
    }

    [Fact]
    public async Task OutboundProxySwitchChangesActualHttpRoutingWithoutRecreatingTheClient()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var config = new CloudDriveAutomationConfig(RssProxyEnabled: true, RssProxyHost: "127.0.0.1", RssProxyPort: port);
        using var client = new HttpClient(OutboundHttpProxy.CreateHandler(() => config));

        var proxiedExchange = ReadRequestAndRespondAsync(listener);
        using (var proxied = await client.GetAsync("http://proxy-target.invalid/first").WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(System.Net.HttpStatusCode.OK, proxied.StatusCode);
        Assert.Equal("GET http://proxy-target.invalid/first HTTP/1.1", await proxiedExchange);

        config = config with { RssProxyEnabled = false };
        var directExchange = ReadRequestAndRespondAsync(listener);
        using (var direct = await client.GetAsync($"http://127.0.0.1:{port}/direct").WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(System.Net.HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal("GET /direct HTTP/1.1", await directExchange);

        config = config with { RssProxyEnabled = true };
        var reenabledExchange = ReadRequestAndRespondAsync(listener);
        using (var reenabled = await client.GetAsync("http://proxy-target.invalid/again").WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(System.Net.HttpStatusCode.OK, reenabled.StatusCode);
        Assert.Equal("GET http://proxy-target.invalid/again HTTP/1.1", await reenabledExchange);
    }

    private static async Task<string> ReadRequestAndRespondAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
        return requestLine ?? string.Empty;
    }

    [Theory]
    [InlineData("ftp://localhost:19798")]
    [InlineData("http://localhost:19798/api")]
    [InlineData("http://localhost:19798/?token=secret")]
    [InlineData("http://user:secret@localhost:19798")]
    public void ConfigRejectsUnsafeEndpointShapes(string endpoint)
    {
        var store = new CloudDriveAutomationStore(Path.Combine(_directory, "cloud.json"));
        Assert.Throws<ArgumentException>(() => store.Save(new CloudDriveAutomationConfig(EndpointUrl: endpoint)));
    }

    [Fact]
    public async Task CredentialsAreDpapiEncryptedAndClearable()
    {
        var path = Path.Combine(_directory, "credentials.bin");
        var store = new CloudDriveCredentialStore(path);
        store.SavePassword("http://localhost:19798", " password-secret ");
        store.SaveToken("http://localhost:19798", "token-secret");

        Assert.Equal("http://localhost:19798", store.Load().EndpointUrl);
        Assert.Equal(" password-secret ", store.Load().Password);
        Assert.Equal("token-secret", store.Load().Token);
        Assert.Throws<InvalidOperationException>(() => store.LoadForEndpoint("https://other.example.test"));
        store.SaveToken("https://other.example.test", "other-token");
        Assert.Null(store.Load().Password);
        Assert.Equal("other-token", store.LoadForEndpoint("https://other.example.test").Token);
        var encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain("password-secret", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", JsonSerializer.Serialize(new { configured = true }), StringComparison.Ordinal);

        store.Clear();
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
