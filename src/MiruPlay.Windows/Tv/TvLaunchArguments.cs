namespace MiruPlay.Windows.Tv;

/// <summary>
/// Parses the TV-mode command line switches. Kept separate from App so the
/// real-user default (--tv must NOT enable the demo key badge) is unit-tested.
/// </summary>
public sealed record TvLaunchOptions(bool StartTv, bool DemoBadge, bool GamepadDemo);

internal static class TvLaunchArguments
{
    public const string TvSwitch = "--tv";
    public const string DemosSwitch = "--tv-demo";

    public const string PadDemoSwitch = "--tv-pad-demo";

    public static TvLaunchOptions Parse(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        var padDemo = values.Contains(PadDemoSwitch, StringComparer.OrdinalIgnoreCase);
        var demo = padDemo || values.Contains(DemosSwitch, StringComparer.OrdinalIgnoreCase);
        var startTv = demo || values.Contains(TvSwitch, StringComparer.OrdinalIgnoreCase);
        return new TvLaunchOptions(startTv, demo, padDemo);
    }
}
