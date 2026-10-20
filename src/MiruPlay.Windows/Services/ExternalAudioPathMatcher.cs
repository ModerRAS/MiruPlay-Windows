namespace MiruPlay.Windows.Services;

internal static class ExternalAudioPathMatcher
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".dts", ".eac3", ".flac", ".m4a", ".mka", ".mp3", ".ogg", ".opus", ".wav",
    };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<string> FindLocalSiblings(string videoPath, int maxCandidates = 1024)
    {
        if (!Path.IsPathRooted(videoPath) || !File.Exists(videoPath)) return [];
        if (videoPath.StartsWith(@"\\", StringComparison.Ordinal)) return [];
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(videoPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
        if (string.IsNullOrEmpty(directory)) return [];
        try
        {
            var candidates = Directory.EnumerateFiles(directory)
                .Where(IsSupported)
                .Take(maxCandidates)
                .ToList();
            return Find(videoPath, candidates);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> Find(string videoPath, IEnumerable<string> siblingPaths)
    {
        var cleanVideo = videoPath.Replace('\\', '/');
        var slash = cleanVideo.LastIndexOf('/');
        var directory = slash < 0 ? "" : cleanVideo[..slash];
        var fileName = slash < 0 ? cleanVideo : cleanVideo[(slash + 1)..];
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length == 0) return [];

        return siblingPaths
            .Select(path => path.Replace('\\', '/'))
            .Where(path => IsSupported(path) &&
                path[..Math.Max(path.LastIndexOf('/'), 0)].Equals(directory, StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var candidate = Path.GetFileNameWithoutExtension(path[(path.LastIndexOf('/') + 1)..]);
                return candidate.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Length > stem.Length && candidate[stem.Length] is '.' or ' ' or '_' or '-' or '[';
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
