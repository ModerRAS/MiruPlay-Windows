using System.Windows.Threading;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

[CollectionDefinition("WpfUi", DisableParallelization = true)]
public sealed class WpfUiCollectionDefinition
{
}

/// <summary>
/// Smoke test for the real borderless TV shell: constructs it on an STA thread
/// with a temporary settings/state database (no user data) and drives it through
/// the same action path remote keys use. It uses the deterministic smoke entry
/// point instead of creating an HWND, so the assertions do not depend on the WPF
/// message pump racing other tests (Application/Dispatcher state in the shared
/// test host).
/// </summary>
[Collection("WpfUi")]
public sealed class TvShellSmokeTests
{
    [Fact]
    public void ShellStartsOnHomeAndNavigatesWithRemoteActions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "TV shell smoke test timed out");
        Assert.Null(failure);
    }

    private static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-tv-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, """{"MediaSourceSchemaVersion":1,"MediaSources":[]}""");
        var context = new TvAppContext(settingsPath, Path.Combine(directory, "state.db"));
        var shell = new TvShellWindow(new TvShellOptions { Context = context, LoadLibrary = false });
        try
        {
            shell.InitializeForSmokeTest();

            Assert.Equal(TvScreenId.Home, shell.Navigator.Current);
            Assert.Equal(TvScreenId.Home, shell.CurrentScreen?.Id);
            Assert.NotNull(shell.CurrentScreen?.ActiveZone);
            Pump();

            shell.SimulateAction(TvInputAction.Down); // hero -> continue row
            shell.SimulateAction(TvInputAction.Up);   // back to hero
            Pump();

            shell.SimulateAction(TvInputAction.Left); // focus navigation rail
            shell.SimulateAction(TvInputAction.Down);
            shell.SimulateAction(TvInputAction.Select);
            Pump();

            Assert.True(
                shell.Navigator.Current == TvScreenId.Library && shell.CurrentScreen?.Id == TvScreenId.Library,
                $"expected Library, navigator={shell.Navigator.Current}, screen={shell.CurrentScreen?.Id}");

            shell.SimulateAction(TvInputAction.Back);
            Pump();
            Assert.True(
                shell.Navigator.Current == TvScreenId.Home,
                $"expected Home after Back, navigator={shell.Navigator.Current}, screen={shell.CurrentScreen?.Id}");
        }
        finally
        {
            shell.Dispose();
            Pump();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
