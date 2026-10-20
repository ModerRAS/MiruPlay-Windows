using System.Configuration;
using System.Data;
using System.Windows;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application, IDisposable
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        AppLocale.Apply(new AppSettingsStore().Load().AppLanguage);
        var window = new MainWindow(background);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        ReleaseInstanceMutex();
        GC.SuppressFinalize(this);
    }

    internal void ReleaseInstanceMutex()
    {
        if (_instanceMutex is null) return;
        _instanceMutex.ReleaseMutex();
        _instanceMutex.Dispose();
        _instanceMutex = null;
    }
}
