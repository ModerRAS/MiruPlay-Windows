using System.Globalization;
using System.Runtime.CompilerServices;

namespace MiruPlay.Windows.Tests;

/// <summary>
/// Message assertions across the suite are written in zh-CN. Pin the test
/// process cultures so localized product strings stay deterministic no
/// matter which OS locale runs the suite.
/// </summary>
internal static class TestCulturePin
{
    [ModuleInitializer]
    internal static void Pin()
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
