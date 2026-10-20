namespace MiruPlay.Windows.Services;

/// <summary>
/// Directory music scan: audio files plus CUE-backed virtual tracks, grouped by
/// parent directory. Results are cached in memory for ten minutes per root;
/// nothing is written beside the media. No tag reader is bundled (dependency
/// light): titles come from CUE sheets, otherwise file names.
/// </summary>
public sealed class MusicLibraryService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".ogg", ".opus", ".m4a", ".aac", ".wav",
    };
    private const int MaxFiles = 20_000;
    private const int MaxDirectories = 2_000;
    private const long MaxCueBytes = 1 * 1024 * 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public sealed record MusicTrack(
        string FilePath,
        int Number,
        string Title,
        string Performer,
        long StartMs,
        long EndMs,
        bool FromCue);
    public sealed record MusicAlbum(string Title, string Directory, IReadOnlyList<MusicTrack> Tracks);

    private readonly object _sync = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<MusicAlbum> Albums)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path));

    public IReadOnlyList<MusicAlbum> Scan(string root, CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(root);
        lock (_sync)
        {
            if (_cache.TryGetValue(fullRoot, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
                return cached.Albums;
        }
        var albums = ScanCore(fullRoot, cancellationToken);
        lock (_sync)
        {
            if (_cache.Count >= 32) _cache.Clear();
            _cache[fullRoot] = (DateTimeOffset.UtcNow, albums);
        }
        return albums;
    }

    public void Invalidate(string root)
    {
        lock (_sync) _cache.Remove(Path.GetFullPath(root));
    }

    private static List<MusicAlbum> ScanCore(string fullRoot, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        var directories = 0;
        while (pending.Count > 0 && files.Count < MaxFiles && directories < MaxDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            directories++;
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                if (IsAudioFile(entry) || Path.GetExtension(entry).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(entry);
                    if (files.Count >= MaxFiles) break;
                }
            }
        }

        var cues = files
            .Where(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimedAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byAlbum = new Dictionary<string, List<MusicTrack>>(StringComparer.OrdinalIgnoreCase);
        var albumDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cuePath in cues.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(cuePath) ?? fullRoot;
            string text;
            try
            {
                if (new FileInfo(cuePath).Length > MaxCueBytes) continue;
                text = File.ReadAllText(cuePath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(cuePath);
            var audio = files.FirstOrDefault(path =>
                !claimedAudio.Contains(path) &&
                IsAudioFile(path) &&
                string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase) &&
                (Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)));
            if (audio is null) continue;
            MusicCueSheet.CueSheet sheet;
            try
            {
                sheet = MusicCueSheet.Parse(text, Path.GetFileName(directory));
            }
            catch (InvalidDataException)
            {
                continue;
            }
            var virtualTracks = MusicCueSheet.Expand(audio, sheet, 0, Path.GetFileName(directory));
            if (virtualTracks.Count == 0) continue;
            claimedAudio.Add(audio);
            var key = directory;
            if (!byAlbum.TryGetValue(key, out var list))
            {
                list = [];
                byAlbum[key] = list;
                albumDirs[key] = directory;
            }
            list.AddRange(virtualTracks.Select(track => new MusicTrack(
                track.AudioPath, track.Number, track.Title, track.Performer, track.StartMs, track.EndMs, true)));
        }

        var loneSequence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var audio in files
            .Where(path => IsAudioFile(path) && !claimedAudio.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(audio) ?? fullRoot;
            if (!byAlbum.TryGetValue(directory, out var list))
            {
                list = [];
                byAlbum[directory] = list;
                albumDirs[directory] = directory;
            }
            loneSequence.TryGetValue(directory, out var sequence);
            sequence++;
            loneSequence[directory] = sequence;
            list.Add(new MusicTrack(
                audio, sequence,
                Path.GetFileNameWithoutExtension(audio) ?? audio,
                "", 0, -1, false));
        }

        return byAlbum
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new MusicAlbum(
                Path.GetFileName(pair.Key) is { Length: > 0 } name ? name : pair.Key,
                albumDirs[pair.Key],
                pair.Value
                    .OrderBy(track => track.Number)
                    .ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }
}
