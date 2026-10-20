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
            throw new ArgumentException(AppStrings.Get("smb.scheme"), nameof(location));
        }
        if (value.StartsWith("//", StringComparison.Ordinal)) value = $"smb:{value}";
        else if (!value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) value = $"smb://{value}";
        if (ContainsUnsafeRawPathSegment(value))
        {
            throw new ArgumentException(AppStrings.Get("smb.unsafe"), nameof(location));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "smb" ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort)
        {
            throw new ArgumentException(AppStrings.Get("smb.shape"), nameof(location));
        }

        var segments = ParseUriSegments(uri);
        if (segments.Count == 0) throw new ArgumentException(AppStrings.Get("smb.needShare"), nameof(location));
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
        if (string.IsNullOrWhiteSpace(indexPath)) throw new InvalidDataException(AppStrings.Get("smb.emptyPath"));
        var clean = indexPath.Trim().Replace('\\', '/');
        if (clean.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(clean, UriKind.Absolute, out _))
        {
            throw new InvalidDataException(AppStrings.Get("smb.absolutePath"));
        }
        var segments = clean.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
        {
            throw new InvalidDataException(AppStrings.Get("smb.unsafePath"));
        }

        var root = ToUncPath(location);
        var resolved = Path.GetFullPath(Path.Combine([root, .. segments]));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppStrings.Get("smb.escaped"));
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
            throw new ArgumentException(AppStrings.Get("smb.unsafe"), nameof(uri));
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
