using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record MpvPlaybackQueueEntry(
    int Season,
    double Number,
    IReadOnlyList<LibraryEpisode> Versions)
{
    public LibraryEpisode DefaultVersion => Versions[0];
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Queue is the playback-domain name exposed to UI integration.")]
public sealed class MpvPlaybackQueue
{
    private readonly IReadOnlyList<MpvPlaybackQueueEntry> _entries;
    private int _index;
    private LibraryEpisode? _selectedVersion;

    public MpvPlaybackQueue(IReadOnlyList<LibraryEpisode> episodes, string currentProgressKey)
    {
        _entries = NextEpisodeResolver.BuildVersionQueue(episodes);
        _index = _entries
            .Select((entry, index) => (entry, index))
            .FirstOrDefault(item => item.entry.Versions.Any(version => version.ProgressKey == currentProgressKey))
            .index;
        if (_entries.Count == 0 || !_entries[_index].Versions.Any(version => version.ProgressKey == currentProgressKey))
        {
            _index = -1;
        }
        else
        {
            _selectedVersion = _entries[_index].Versions.First(version => version.ProgressKey == currentProgressKey);
        }
    }

    public IReadOnlyList<MpvPlaybackQueueEntry> Entries => _entries;
    public MpvPlaybackQueueEntry? Current => _index >= 0 ? _entries[_index] : null;
    public LibraryEpisode? CurrentVersion => _selectedVersion ?? Current?.DefaultVersion;
    public bool CanPlayPrevious => _index > 0;
    public bool CanPlayNext => _index >= 0 && _index + 1 < _entries.Count;

    public LibraryEpisode? Previous() => Move(-1);

    public LibraryEpisode? Next() => Move(1);

    private LibraryEpisode? Move(int offset)
    {
        if (offset < 0 ? !CanPlayPrevious : !CanPlayNext) return null;
        var currentPath = CurrentVersion?.MediaPath;
        _index += offset;
        _selectedVersion = currentPath is null ? null : NearestVersion(_entries[_index], currentPath);
        return CurrentVersion;
    }

    public LibraryEpisode? SelectVersion(string mediaPath)
    {
        var current = Current;
        if (current is null) return null;
        _selectedVersion = NearestVersion(current, mediaPath);
        return _selectedVersion;
    }

    public void ResetTo(string progressKey)
    {
        var index = _entries
            .Select((entry, index) => (entry, index))
            .FirstOrDefault(item => item.entry.Versions.Any(version => version.ProgressKey == progressKey))
            .index;
        _index = index >= 0 && index < _entries.Count && _entries[index].Versions.Any(version => version.ProgressKey == progressKey)
            ? index
            : -1;
        _selectedVersion = null;
    }

    private static LibraryEpisode? NearestVersion(MpvPlaybackQueueEntry entry, string mediaPath) =>
        entry.Versions
            .OrderByDescending(version => CommonPathSegmentCount(mediaPath, version.MediaPath))
            .ThenByDescending(version => CommonPrefixLength(mediaPath, version.MediaPath))
            .ThenBy(version => version.MediaPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static int CommonPathSegmentCount(string left, string right)
    {
        var leftParts = PathSegments(left);
        var rightParts = PathSegments(right);
        return leftParts.Zip(rightParts).TakeWhile(parts =>
            parts.First.Equals(parts.Second, StringComparison.OrdinalIgnoreCase)).Count();
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var length = 0;
        while (length < left.Length && length < right.Length &&
            char.ToUpperInvariant(left[length]) == char.ToUpperInvariant(right[length])) length++;
        return length;
    }

    private static string[] PathSegments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
}
