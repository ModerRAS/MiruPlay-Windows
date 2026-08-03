using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record CloudDriveVideoClassification(string ShowName, int SeasonNumber, double? EpisodeNumber = null);

public sealed class CloudDriveLibraryOrganizer
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts",
    };
    private readonly CloudDriveGrpcClient _cloudDrive;
    private readonly IAnimeVideoClassifier _classifier;

    public CloudDriveLibraryOrganizer(
        CloudDriveGrpcClient cloudDrive,
        IAnimeVideoClassifier? classifier = null)
    {
        _cloudDrive = cloudDrive;
        _classifier = classifier ?? SharedAnimeVideoClassifier.Instance;
    }

    public async Task<int> OrganizeAsync(
        CloudDriveAutomationConfig config,
        CloudDriveTokenInfo tokenInfo,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!tokenInfo.AllowList || !tokenInfo.AllowCreateFolder || !tokenInfo.AllowMove)
            throw new InvalidOperationException("CloudDrive2 API Token 缺少媒体整理所需的列表、建目录或移动权限。");
        var root = NormalizePath(tokenInfo.RootDir);
        var inbox = NormalizePath(config.InboxPath);
        var library = NormalizePath(config.LibraryPath);
        if (inbox == "/" || library == "/") throw new InvalidOperationException("CloudDrive2 下载目录和整理目录不能是根目录。");
        if (!IsSameOrChild(inbox, root) || !IsSameOrChild(library, root)) throw new InvalidOperationException("CloudDrive2 整理目录超出 Token 根目录。");
        if (IsSameOrChild(library, inbox)) throw new InvalidOperationException("CloudDrive2 整理目录不能位于下载目录内部。");
        var videos = await CollectVideosAsync(config.EndpointUrl, token, inbox, 0, cancellationToken).ConfigureAwait(false);
        var moved = 0;
        foreach (var video in videos)
        {
            if (!IsChild(video.Path, inbox)) continue;
            var classification = _classifier.Classify(
                video.Path,
                video.Name,
                ParentPath(video.Path).Split('/').LastOrDefault());
            var showFolder = SafeFolderSegment(classification.ShowName);
            var seasonFolder = $"Season {Math.Max(1, classification.SeasonNumber)}";
            var showPath = $"{library}/{showFolder}";
            var seasonPath = $"{showPath}/{seasonFolder}";
            await EnsureFolderAsync(config.EndpointUrl, token, library, showFolder, cancellationToken).ConfigureAwait(false);
            await EnsureFolderAsync(config.EndpointUrl, token, showPath, seasonFolder, cancellationToken).ConfigureAwait(false);
            await _cloudDrive.MoveFilesAsync(config.EndpointUrl, token, [video.Path], seasonPath, cancellationToken).ConfigureAwait(false);
            moved++;
        }
        return moved;
    }

    private async Task<List<CloudDriveFileInfo>> CollectVideosAsync(
        string endpoint,
        string token,
        string path,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 5) return [];
        var entries = await _cloudDrive.ListFolderAsync(endpoint, token, path, true, cancellationToken).ConfigureAwait(false);
        var videos = new List<CloudDriveFileInfo>();
        foreach (var entry in entries)
        {
            var entryPath = NormalizePath(entry.Path);
            if (!IsSameOrChild(entryPath, path) || entry.Name.StartsWith('.') || entry.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.IsDirectory)
                videos.AddRange(await CollectVideosAsync(endpoint, token, entryPath, depth + 1, cancellationToken).ConfigureAwait(false));
            else if (VideoExtensions.Contains(Path.GetExtension(entry.Name)))
                videos.Add(entry with { Path = entryPath });
        }
        return videos;
    }

    private async Task EnsureFolderAsync(
        string endpoint,
        string token,
        string parentPath,
        string folderName,
        CancellationToken cancellationToken)
    {
        var entries = await _cloudDrive.ListFolderAsync(endpoint, token, parentPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!entries.Any(entry => entry.IsDirectory && entry.Name == folderName))
            await _cloudDrive.CreateFolderAsync(endpoint, token, parentPath, folderName, cancellationToken).ConfigureAwait(false);
    }

    private static string SafeFolderSegment(string value)
    {
        var safe = Regex.Replace(value, "[\\\\/:*?\"<>|]", "_", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)).Trim();
        return safe.Length == 0 ? "Unknown" : safe;
    }

    private static string NormalizePath(string path)
    {
        var segments = path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) throw new InvalidOperationException("CloudDrive2 目录包含路径遍历。");
        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static bool IsSameOrChild(string path, string parent) =>
        parent == "/" || path == parent || path.StartsWith($"{parent}/", StringComparison.Ordinal);

    private static bool IsChild(string path, string parent) => path.StartsWith($"{parent.TrimEnd('/')}/", StringComparison.Ordinal);
}

public static class VideoFilenameInference
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex LeadingGroup = new("^\\s*(?:\\[[^\\]]+]|【[^】]+】|\\([^)]+\\))\\s*", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Tags = new("[\\[(【][^\\])】]{1,64}[\\])】]", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex SeasonEpisode = new("(?:^|[\\s._-])S(\\d{1,2})E(\\d{1,3})(?:[\\s._-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex EpisodeNumber = new("(?:^|[\\s._-])(?:EP?)?(\\d{1,4})(?:v\\d+)?(?:[\\s._-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex CleanupSeparators = new("[_・]+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex TrailingDash = new("\\s*[-–—]\\s*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex GenericSeparators = new("[._\\-\\[\\]【】()（）]+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly HashSet<string> GenericParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "115open", "ani", "anime", "anime library", "download", "downloads", "library", "media", "video", "videos", "动漫", "下载", "下載",
    };

    public static CloudDriveVideoClassification Classify(string fileName, string? parentName = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var withoutTags = Tags.Replace(LeadingGroup.Replace(stem, ""), " ");
        var match = SeasonEpisode.Match(withoutTags);
        if (!match.Success) match = EpisodeNumber.Matches(withoutTags).LastOrDefault() ?? Match.Empty;
        var titlePart = match.Success ? withoutTags[..match.Index] : withoutTags;
        var parent = Cleanup(parentName ?? "");
        if (GenericParents.Contains(GenericSeparators.Replace(parent, " ").Trim())) parent = "";
        var title = Cleanup(titlePart);
        if (title.Length == 0) title = parent.Length == 0 ? "Unknown" : parent;
        var seasonMatch = SeasonEpisode.Match(stem);
        var season = seasonMatch.Success && int.TryParse(seasonMatch.Groups[1].Value, out var number) ? number : 1;
        double? episode = match.Success && double.TryParse(
            match.Groups[match.Groups.Count - 1].Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedEpisode)
            ? parsedEpisode
            : null;
        return new CloudDriveVideoClassification(title, season, episode);
    }

    private static string Cleanup(string value) =>
        Whitespace.Replace(TrailingDash.Replace(CleanupSeparators.Replace(value, " "), ""), " ").Trim();
}
