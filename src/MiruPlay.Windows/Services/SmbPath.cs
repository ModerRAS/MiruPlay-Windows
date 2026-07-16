namespace MiruPlay.Windows.Services;

public static class SmbPath
{
    public static string NormalizeRoot(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var value = location.Trim().Replace('\\', '/').TrimEnd('/');
        if (value.Contains("://", StringComparison.Ordinal) &&
            !value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SMB 地址必须使用 smb:// scheme。", nameof(location));
        }
        if (value.StartsWith("//", StringComparison.Ordinal)) value = $"smb:{value}";
        else if (!value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) value = $"smb://{value}";
        if (ContainsUnsafeRawPathSegment(value))
        {
            throw new ArgumentException("SMB 地址包含不安全的目录段。", nameof(location));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "smb" ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort)
        {
            throw new ArgumentException("SMB 地址必须是 smb://服务器/共享目录 或 UNC 路径。", nameof(location));
        }

        var segments = ParseUriSegments(uri);
        if (segments.Count == 0) throw new ArgumentException("SMB 地址必须包含共享目录。", nameof(location));
        return $"smb://{uri.IdnHost}/{string.Join('/', segments.Select(Uri.EscapeDataString))}";
    }

    public static string ToUncPath(string location)
    {
        var normalized = NormalizeRoot(location);
        var uri = new Uri(normalized);
        return $"\\\\{uri.IdnHost}\\{string.Join('\\', ParseUriSegments(uri))}";
    }

    public static string ShareRoot(string location)
    {
        var normalized = NormalizeRoot(location);
        var uri = new Uri(normalized);
        return $"\\\\{uri.IdnHost}\\{ParseUriSegments(uri)[0]}";
    }

    public static string ResolveIndexPath(string location, string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath)) throw new InvalidDataException("MLIP 路径不能为空。");
        var clean = indexPath.Trim().Replace('\\', '/');
        if (clean.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(clean, UriKind.Absolute, out _))
        {
            throw new InvalidDataException("MLIP 路径不能是绝对地址。");
        }
        var segments = clean.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
        {
            throw new InvalidDataException("MLIP 路径包含不安全的目录段。");
        }

        var root = ToUncPath(location);
        var resolved = Path.GetFullPath(Path.Combine([root, .. segments]));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("MLIP 路径越过了 SMB 媒体源根目录。");
        }
        return resolved;
    }

    private static List<string> ParseUriSegments(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();
        if (segments.Any(IsUnsafeSegment))
        {
            throw new ArgumentException("SMB 地址包含不安全的目录段。", nameof(uri));
        }
        return segments;
    }

    private static bool ContainsUnsafeRawPathSegment(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = value.IndexOf('/', authorityStart);
        if (pathStart < 0) return false;
        return value[(pathStart + 1)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .Any(segment => segment is "." or ".." || segment.IndexOfAny(['/', '\\', '\0']) >= 0);
    }

    private static bool IsUnsafeSegment(string segment) =>
        segment is "." or ".." ||
        string.IsNullOrWhiteSpace(segment) ||
        segment.IndexOfAny(['/', '\\', ':', '\0']) >= 0;
}
