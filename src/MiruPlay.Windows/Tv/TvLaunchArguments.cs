namespace MiruPlay.Windows.Tv;

/// <summary>
/// Parses the TV-mode command line switches. Kept separate from App so the
/// real-user default (--tv must NOT enable the demo key badge) is unit-tested.
/// </summary>
internal static class TvLaunchArguments
{
    public const string TvSwitch = "--tv";
    public const string DemosSwitch = "--tv-demo";

    public static (bool StartTv, bool DemoBadge) Parse(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        var demo = values.Contains(DemosSwitch, StringComparer.OrdinalIgnoreCase);
        var startTv = demo || values.Contains(TvSwitch, StringComparer.OrdinalIgnoreCase);
        return (startTv, demo);
    }
}
