using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

/// <summary>Linear queue over music pseudo-episodes for whole-album autoplay.
/// Unlike <see cref="MpvPlaybackQueue"/>, music tracks carry their cue range
/// in the episode itself, so advancing is a plain index move.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Queue is the playback-domain name exposed to UI integration.")]
public sealed class MusicPlaybackQueue
{
    private readonly IReadOnlyList<LibraryEpisode> _tracks;
    private int _index;

    public MusicPlaybackQueue(IReadOnlyList<LibraryEpisode> tracks, string currentProgressKey)
    {
        _tracks = tracks;
        _index = -1;
        for (var i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].ProgressKey == currentProgressKey)
            {
                _index = i;
                break;
            }
        }
    }

    public int Count => _tracks.Count;
    public LibraryEpisode? Current => _index >= 0 ? _tracks[_index] : null;
    public bool CanPlayNext => _index >= 0 && _index + 1 < _tracks.Count;

    public LibraryEpisode? Next()
    {
        if (!CanPlayNext) return null;
        _index++;
        return _tracks[_index];
    }

    public void ResetTo(string progressKey)
    {
        _index = -1;
        for (var i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].ProgressKey == progressKey)
            {
                _index = i;
                break;
            }
        }
    }
}
