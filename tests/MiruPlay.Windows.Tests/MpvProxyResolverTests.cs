using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvProxyResolverTests
{
    private static readonly SystemProxySettings SystemOn = new(true, "127.0.0.1:7890");
    private const string Remote = "https://resources.example.test/video.mp4?d=mp4";

    [Fact]
    public void PrefersConfiguredAppProxy()
    {
        var config = new CloudDriveAutomationConfig(RssProxyEnabled: true, RssProxyHost: "127.0.0.1", RssProxyPort: 7891);
        var proxy = MpvProxyResolver.Resolve(new AppSettings(), config, Remote, () => SystemOn);
        Assert.Equal("http://127.0.0.1:7891", proxy);
    }

    [Fact]
    public void FallsBackToSystemProxyWhenAppProxyDisabled()
    {
        var config = new CloudDriveAutomationConfig(RssProxyEnabled: false);
        Assert.Equal("http://127.0.0.1:7890", MpvProxyResolver.Resolve(new AppSettings(), config, Remote, () => SystemOn));
    }

    [Fact]
    public void InvalidAppProxyFallsBackToSystemProxy()
    {
        var config = new CloudDriveAutomationConfig(RssProxyEnabled: true, RssProxyHost: "bad host", RssProxyPort: 7891);
        Assert.Equal("http://127.0.0.1:7890", MpvProxyResolver.Resolve(new AppSettings(), config, Remote, () => SystemOn));
    }

    [Fact]
    public void DisabledPlaybackSystemProxySkipsSystemFallback()
    {
        var settings = new AppSettings(PlaybackUseSystemProxy: false);
        Assert.Null(MpvProxyResolver.Resolve(settings, new CloudDriveAutomationConfig(), Remote, () => SystemOn));
    }

    [Fact]
    public void LoopbackAndLocalMediaNeverProxy()
    {
        Assert.Null(MpvProxyResolver.Resolve(new AppSettings(), new CloudDriveAutomationConfig(), "http://127.0.0.1:19798/media.mp4", () => SystemOn));
        Assert.Null(MpvProxyResolver.Resolve(new AppSettings(), new CloudDriveAutomationConfig(), @"C:\media\local.mp4", () => SystemOn));
        Assert.Null(MpvProxyResolver.Resolve(new AppSettings(), new CloudDriveAutomationConfig(), "https://[::1]/media.mp4", () => SystemOn));
    }

    [Theory]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("http=127.0.0.1:7890;https=127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("https=127.0.0.1:7890;http=127.0.0.1:7891", "http://127.0.0.1:7891")]
    [InlineData("http://proxy.example:8080", "http://proxy.example:8080")]
    public void ParsesSystemProxyServerFormats(string server, string expected)
    {
        Assert.True(MpvProxyResolver.TryParseSystemProxyServer(server, out var proxy));
        Assert.Equal(expected, proxy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("socks=127.0.0.1:7890")]
    [InlineData("socks=127.0.0.1:7890;https=127.0.0.1:7891")]
    [InlineData("127.0.0.1:notaport")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("bad host:7890")]
    public void RejectsInvalidOrSocksOnlyProxyServers(string server) =>
        Assert.False(MpvProxyResolver.TryParseSystemProxyServer(server, out _));

    [Fact]
    public void RedactsUrlsToHostOnly()
    {
        var text = "failed https://resources.example.test/path/video.mp4?token=secret and http://other.example:8080/a";
        var redacted = MpvProxyResolver.RedactUrlsToHosts(text);
        Assert.Contains("https://resources.example.test/...", redacted);
        Assert.Contains("http://other.example:8080/...", redacted);
        Assert.DoesNotContain("video.mp4", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("/path/", redacted);
    }

    [Fact]
    public void HostOfExtractsHostOnly()
    {
        Assert.Equal("resources.example.test", MpvProxyResolver.HostOf(Remote));
        Assert.Null(MpvProxyResolver.HostOf(@"C:\media\local.mp4"));
    }
}
