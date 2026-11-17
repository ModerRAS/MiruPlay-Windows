using System.Windows;
using System.Windows.Threading;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

/// <summary>
/// Creates the real borderless TV shell on an STA thread with a temporary
/// settings/state database (no user data), then drives it through the same
/// action path remote keys use.
/// </summary>
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
            shell.Show();
            Pump();

            Assert.Equal(TvScreenId.Home, shell.Navigator.Current);
            Assert.Equal(TvScreenId.Home, shell.CurrentScreen?.Id);
            Assert.NotNull(shell.CurrentScreen?.ActiveZone);

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
            shell.Close();
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