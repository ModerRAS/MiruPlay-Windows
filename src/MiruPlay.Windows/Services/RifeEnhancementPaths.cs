using System.Globalization;

namespace MiruPlay.Windows.Services;

/// <summary>Filesystem and mpv path conventions for the RIFE enhancement pack.
/// Installed (downloaded) pack lives under %LOCALAPPDATA%/MiruPlay/enhance/rife;
/// the full edition bundles the identical layout under &lt;app&gt;/runtime/enhance.</summary>
public static class RifeEnhancementPaths
{
    public const string PackDirectoryName = "rife";
    public const string ManifestFileName = "manifest.json";
    public const string RifePluginFileName = "librife_windows_x86-64.dll";

    public static string LocalEnhanceRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "enhance");

    public static string InstalledRoot => Path.Combine(LocalEnhanceRoot, PackDirectoryName);

    public static string StatePath => Path.Combine(LocalEnhanceRoot, "rife-state.json");

    public static string ScriptDirectory => Path.Combine(LocalEnhanceRoot, "scripts");

    public static string MirrorRoot => Path.Combine(LocalEnhanceRoot, "rife-ascii");

    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "runtime", "enhance", "rife");

    public static string ManifestPath(string packRoot) => Path.Combine(packRoot, ManifestFileName);

    public static string VSScriptPath(string packRoot) => Path.Combine(packRoot, "VSScript.dll");

    public static string VSPipePath(string packRoot) => Path.Combine(packRoot, "VSPipe.exe");

    public static string VsPluginsDirectory(string packRoot) => Path.Combine(packRoot, "vs-plugins");

    public static string RifePluginPath(string packRoot) => Path.Combine(packRoot, "vs-plugins", RifePluginFileName);

    public static string RifeModelsPath(string packRoot) => Path.Combine(packRoot, "vs-plugins", "models");

    /// <summary>mpv filter arguments only accept forward slashes; backslashes are escapes.</summary>
    public static string ToMpvPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    public static bool IsAsciiPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.All(character => character < 0x80);

    public static bool IsPackPresent(string packRoot) =>
        File.Exists(ManifestPath(packRoot)) &&
        File.Exists(VSScriptPath(packRoot)) &&
        File.Exists(VSPipePath(packRoot)) &&
        File.Exists(RifePluginPath(packRoot)) &&
        Directory.Exists(RifeModelsPath(packRoot));

    public static string FormatFactor(int factor) => factor.ToString(CultureInfo.InvariantCulture) + "x";
}
