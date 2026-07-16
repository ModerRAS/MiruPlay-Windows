namespace MiruPlay.Windows.Services;

public static class MlipPath
{
    public static string ResolveLocal(string rootPath, string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        if (Uri.TryCreate(indexPath, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            throw new InvalidDataException($"MLIP path must be local: {indexPath}");
        }

        var segments = indexPath
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains("://", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Unsafe MLIP path: {indexPath}");
        }

        var root = Path.GetFullPath(rootPath);
        var resolved = Path.GetFullPath(Path.Combine([root, .. segments]));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"MLIP path escapes the library root: {indexPath}");
        }
        RejectReparsePoints(root, segments, indexPath);

        return resolved;
    }

    private static void RejectReparsePoints(string root, IReadOnlyList<string> segments, string indexPath)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"MLIP path traverses a reparse point: {indexPath}");
            }
        }
    }

    public static string ResolveRemote(string rootUrl, string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootUrl);
        var root = WebDavMlipClient.NormalizeRoot(rootUrl);
        var segments = SafeSegments(indexPath);
        var encodedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri(root, encodedPath).AbsoluteUri;
    }

    private static string[] SafeSegments(string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        if (Uri.TryCreate(indexPath, UriKind.Absolute, out _))
        {
            throw new InvalidDataException($"MLIP path must be relative: {indexPath}");
        }
        var segments = indexPath
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains("://", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Unsafe MLIP path: {indexPath}");
        }
        return segments;
    }
}
