namespace MiruPlay.Windows.Tv;

public enum TvInputOrigin
{
    Keyboard,
    Gamepad,
}

/// <summary>
/// Suppresses the same action arriving from keyboard and gamepad at essentially
/// the same moment (e.g. pressing ← on both devices) so the UI does not double
/// step, while still allowing genuine rapid repeated presses from either side.
/// </summary>
public sealed class TvActionCoalescer
{
    public const int CrossOriginWindowMs = 60;
    private readonly Dictionary<TvInputAction, (TvInputOrigin Origin, long At)> _last = [];

    public bool ShouldDispatch(TvInputAction action, TvInputOrigin origin, long nowMs)
    {
        if (_last.TryGetValue(action, out var previous) &&
            previous.Origin != origin &&
            nowMs - previous.At < CrossOriginWindowMs)
        {
            return false;
        }
        _last[action] = (origin, nowMs);
        return true;
    }

    public void Reset() => _last.Clear();
}
