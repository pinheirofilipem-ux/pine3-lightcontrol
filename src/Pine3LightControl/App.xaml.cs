using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Pine3LightControl.Bluetooth;
using Pine3LightControl.Core;
using Pine3LightControl.Core.Lights;

namespace Pine3LightControl;

public partial class App : Application
{
    private const string MutexName = "Local\\Pine3LightControl.SingleInstance";
    private const string ShowEventName = "Local\\Pine3LightControl.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _main;

    public static LightHub Hub { get; private set; } = null!;
    public static AppConfig Config => Hub.Config;
    internal static TrayIcon? Tray { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        // ── Instância única: se já está a correr, pede-lhe para mostrar a janela ──
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(ShowMain);
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"[UI] exceção: {ex.Exception}");
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write($"[Task] {ex.Exception.GetBaseException().Message}"); ex.SetObserved(); };

        Log.Write($"[App] Pine3 Light Control {typeof(App).Assembly.GetName().Version} a arrancar");
        Dpapi.Install();
        var config = AppConfig.Load();
        config.StartWithWindows = StartupManager.IsEnabled();
        Appearance.Apply(config.Appearance);

        Hub = new LightHub(config, d => new WinBleLink(d.Address, string.IsNullOrWhiteSpace(d.Name) ? d.Address : d.Name));
        Tray = new TrayIcon(Hub, ShowMain, Quit);
        await Hub.StartAsync();

        // Arranque manual abre sempre a janela; com o Windows (--minimized) fica na tray.
        if (!minimized || !config.StartMinimized) ShowMain();
    }

    public void ShowMain()
    {
        _main ??= new MainWindow(Hub);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;      // traz para a frente mesmo vindo da tray
        _main.Topmost = false;
        _main.Focus();
    }

    public async void Quit()
    {
        Log.Write("[App] a sair");
        _main?.CloseForQuit();
        Tray?.Dispose();
        try { await Hub.DisposeAsync(); } catch { }
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        Shutdown();
    }
}
