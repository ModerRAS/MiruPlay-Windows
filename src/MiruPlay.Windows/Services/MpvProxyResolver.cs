using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MiruPlay.Windows.Services;

public sealed record SystemProxySettings(bool Enabled, string? Server);

/// <summary>Resolves the proxy passed to libmpv's http-proxy option for remote HTTP(S) media.</summary>
public static class MpvProxyResolver
{
    public static string? Resolve(
        AppSettings settings,
        CloudDriveAutomationConfig? appConfig,
        string? mediaPath,
        Func<SystemProxySettings?>? readSystemProxy = null)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) ||
            !Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.IsLoopback)
            return null;

        // 1) The App's configured outbound RSS/CloudDrive proxy wins.
        if (appConfig is { RssProxyEnabled: true } &&
            TryNormalizeHostAndPort(appConfig.RssProxyHost, appConfig.RssProxyPort, out var appProxy))
            return appProxy;

        // 2) Fall back to the Windows system proxy unless the user disabled it.
        if (!settings.PlaybackUseSystemProxy) return null;
        var system = (readSystemProxy ?? SystemProxyReader.Read)();
        if (system is not { Enabled: true } || string.IsNullOrWhiteSpace(system.Server)) return null;
        return TryParseSystemProxyServer(system.Server, out var systemProxy) ? systemProxy : null;
    }

    public static bool TryNormalizeHostAndPort(string? host, int port, out string proxy)
    {
        proxy = string.Empty;
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65_535) return false;
        try
        {
            var uri = OutboundHttpProxy.ValidateUri(host, port);
            proxy = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parses ProxyServer formats: "host:port", "http=host:port;https=..." or "socks=..." (ignored).</summary>
    public static bool TryParseSystemProxyServer(string? server, out string proxy)
    {
        proxy = string.Empty;
        if (string.IsNullOrWhiteSpace(server)) return false;
        string? httpValue = null, plainValue = null, httpsValue = null;
        foreach (var rawSegment in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = rawSegment.Trim();
            if (segment.StartsWith("socks=", StringComparison.OrdinalIgnoreCase)) continue;
            if (segment.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                httpValue ??= segment["http=".Length..].Trim();
            else if (segment.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                httpsValue ??= segment["https=".Length..].Trim();
            else
                plainValue ??= segment;
        }
        foreach (var candidate in new[] { httpValue, plainValue })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (TryParseProxyEndpoint(candidate, out proxy)) return true;
        }
        proxy = string.Empty;
        return false;
    }

    private static bool TryParseProxyEndpoint(string value, out string proxy)
    {
        proxy = string.Empty;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port is < 1 or > 65_535)
                return false;
            proxy = $"http://{uri.Host}:{uri.Port}";
            return true;
        }
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;
        var host = value[..separator].Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        return int.TryParse(value[(separator + 1)..], out var port) &&
            TryNormalizeHostAndPort(host, port, out proxy);
    }

    public static string? HostOf(string? mediaPath) =>
        Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri.Host
            : null;

    /// <summary>Keeps only scheme + host when placing native mpv logs into exceptions/JSONL.</summary>
    public static string RedactUrlsToHosts(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : FullUrl.Replace(text, match => $"{match.Groups["scheme"].Value}://{match.Groups["host"].Value}/...");

    private static readonly Regex FullUrl = new(
        @"\b(?<scheme>https?)://(?<host>[^/\s""'<>]+)[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

public static class SystemProxyReader
{
    public static SystemProxySettings? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;
            var enabled = key.GetValue("ProxyEnable") switch
            {
                int value => value == 1,
                long value => value == 1,
                string value => value.Trim() == "1",
                _ => false,
            };
            var server = key.GetValue("ProxyServer") as string;
            return new SystemProxySettings(enabled, server);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException or InvalidCastException)
        {
            return null;
        }
    }
}