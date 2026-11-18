using System.Configuration;
using System.Data;
using System.Windows;
using MiruPlay.Windows.Services;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application, IDisposable
{
    private Mutex? _instanceMutex;
    private MainWindow? _desktopWindow;
    private TvShellWindow? _tvWindow;
    private bool _tvDemo;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => LogCrash("dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("domain", args.ExceptionObject as Exception);
        _instanceMutex = new Mutex(true, $"Local\\MiruPlay.Windows.{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var tvLaunch = TvLaunchArguments.Parse(e.Args);
        _tvDemo = tvLaunch.DemoBadge;
        var forceTv = tvLaunch.StartTv;
        AppLocale.Apply(new AppSettingsStore().Load().AppLanguage);
        _desktopWindow = new MainWindow(background);
        MainWindow = _desktopWindow;
        if (!forceTv)
        {
            var settings = new AppSettingsStore().Load();
            if (settings.TvModeEnabled) forceTv = true;
        }
        if (forceTv) EnterTvMode(tvLaunch.DemoBadge, tvLaunch.GamepadDemo);
        else _desktopWindow.Show();
    }

    /// <summary>Hides the desktop shell and opens the borderless TV shell in this process.</summary>
    internal void EnterTvMode(bool demo = false, bool gamepadDemo = false)
    {
        if (_tvWindow is not null || _shuttingDown) return;
        _desktopWindow?.Hide();
        var shell = new TvShellWindow(new TvShellOptions
        {
            DemoMode = demo || _tvDemo,
            GamepadDemo = gamepadDemo,
            ExitRequested = ReturnToDesktop,
        });
        _tvWindow = shell;
        shell.Closed += (_, _) =>
        {
            if (ReferenceEquals(_tvWindow, shell)) _tvWindow = null;
        };
        shell.Show();
        shell.Activate();
    }

    private void ReturnToDesktop()
    {
        _tvWindow = null;
        if (_shuttingDown || _desktopWindow is null) return;
        _desktopWindow.Show();
        _desktopWindow.WindowState = WindowState.Normal;
        _desktopWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? error)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "miruplay-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}: {error}\n\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Crash logging must never mask the original failure.
        }
    }

    public void Dispose()
    {
        ReleaseInstanceMutex();
        GC.SuppressFinalize(this);
    }

    internal void ReleaseInstanceMutex()
    {
        if (_instanceMutex is null) return;
        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex was not owned by this thread during shutdown; nothing to release.
        }
        _instanceMutex.Dispose();
        _instanceMutex = null;
    }
}
