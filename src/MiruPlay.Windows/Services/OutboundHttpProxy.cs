using System.Net;

namespace MiruPlay.Windows.Services;

public static class OutboundHttpProxy
{
    public static HttpClientHandler CreateHandler(Func<CloudDriveAutomationConfig> getConfig) => new()
    {
        Proxy = new DynamicProxy(getConfig),
        UseProxy = true,
    };

    public static HttpClientHandler CreateHandler(bool enabled, string host, int port)
    {
        var handler = new HttpClientHandler();
        if (!enabled) return handler;
        handler.Proxy = new WebProxy(ValidateUri(host, port));
        handler.UseProxy = true;
        return handler;
    }

    private sealed class DynamicProxy(Func<CloudDriveAutomationConfig> getConfig) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => getConfig() is { RssProxyEnabled: true } config
            ? ValidateUri(config.RssProxyHost, config.RssProxyPort)
            : destination;

        public bool IsBypassed(Uri host) => !getConfig().RssProxyEnabled;
    }

    public static Uri ValidateUri(string host, int port)
    {
        var value = host.Trim();
        if (port is < 1 or > 65_535 || value.Length is 0 or > 253 ||
            value.Any(char.IsWhiteSpace) || value.Contains('/') || value.Contains('\\') || value.Contains('@') ||
            value.Contains('?') || value.Contains('#') || value.Contains("://", StringComparison.Ordinal) ||
            Uri.CheckHostName(value) == UriHostNameType.Unknown ||
            !Uri.TryCreate(new UriBuilder(Uri.UriSchemeHttp, value, port).Uri.AbsoluteUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new ArgumentException("代理地址必须是无路径、凭据、查询或片段的 HTTP 主机和有效端口。", nameof(host));
        return uri;
    }
}
