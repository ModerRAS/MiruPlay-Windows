using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiruPlay.Windows.Services;

[JsonConverter(typeof(JsonStringEnumConverter<CloudDriveLibraryMode>))]
public enum CloudDriveLibraryMode
{
    [JsonStringEnumMemberName("ORGANIZED_LIBRARY")]
    OrganizedLibrary,
    [JsonStringEnumMemberName("SINGLE_DIRECTORY")]
    SingleDirectory,
}

public sealed record CloudDriveConfigRequest(
    string EndpointUrl,
    string Username,
    long? WebDavSourceId,
    string InboxPath,
    string LibraryPath,
    CloudDriveLibraryMode LibraryMode = CloudDriveLibraryMode.OrganizedLibrary,
    int IntervalMinutes = 30,
    bool Enabled = false,
    bool RssProxyEnabled = false,
    string RssProxyHost = "",
    int RssProxyPort = 1080);

public sealed record CloudDriveAutomationConfig(
    string EndpointUrl = "http://localhost:19798",
    string Username = "",
    long? WebDavSourceId = null,
    string InboxPath = "",
    string LibraryPath = "",
    CloudDriveLibraryMode LibraryMode = CloudDriveLibraryMode.OrganizedLibrary,
    int IntervalMinutes = 30,
    bool Enabled = false,
    long LastRunAt = 0,
    bool RssProxyEnabled = false,
    string RssProxyHost = "",
    int RssProxyPort = 1080);

public sealed class CloudDriveAutomationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public CloudDriveAutomationStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "cloud-drive.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
    }

    public CloudDriveAutomationConfig Load()
    {
        if (!File.Exists(_path)) return new CloudDriveAutomationConfig();
        try
        {
            return JsonSerializer.Deserialize<CloudDriveAutomationConfig>(File.ReadAllText(_path), JsonOptions)
                ?? new CloudDriveAutomationConfig();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("CloudDrive 配置损坏。", error);
        }
    }

    public CloudDriveAutomationConfig Save(CloudDriveAutomationConfig config)
    {
        var normalized = Normalize(config);
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, _path, true);
        return normalized;
    }

    private static CloudDriveAutomationConfig Normalize(CloudDriveAutomationConfig config)
    {
        var endpoint = config.EndpointUrl.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.AbsolutePath is not ("" or "/")) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("CloudDrive2 地址必须是无路径、查询、片段或嵌入凭据的 HTTP(S) 服务根地址。", nameof(config));
        var username = config.Username.Trim();
        if (username.Length > 100) throw new ArgumentException("CloudDrive2 用户名不能超过 100 个字符。", nameof(config));
        var inbox = NormalizePath(config.InboxPath, "离线下载目录");
        var library = NormalizePath(config.LibraryPath, "媒体库目录");
        var proxyHost = config.RssProxyHost.Trim();
        if (proxyHost.Length > 253) throw new ArgumentException("RSS 代理主机名过长。", nameof(config));
        if (config.Enabled && (inbox.Length == 0 || library.Length == 0))
            throw new ArgumentException("启用 CloudDrive/RSS 前必须设置离线下载目录和媒体库目录。", nameof(config));
        return config with
        {
            EndpointUrl = uri.GetLeftPart(UriPartial.Authority),
            Username = username,
            WebDavSourceId = config.WebDavSourceId is > 0 ? config.WebDavSourceId : null,
            InboxPath = inbox,
            LibraryPath = library,
            IntervalMinutes = Math.Max(15, config.IntervalMinutes),
            RssProxyHost = proxyHost,
            RssProxyPort = Math.Clamp(config.RssProxyPort, 1, 65_535),
        };
    }

    private static string NormalizePath(string value, string label)
    {
        var path = value.Trim();
        if (path.Length > 1_024) throw new ArgumentException($"{label}不能超过 1024 个字符。", nameof(value));
        return path;
    }
}
