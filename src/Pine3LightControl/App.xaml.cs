using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        // ── Comandos do instalador: registam/removem o arranque e saem ──
        bool reg = e.Args.Any(a => a.Equals("--register-startup", StringComparison.OrdinalIgnoreCase));
        if (reg || e.Args.Any(a => a.Equals("--unregister-startup", StringComparison.OrdinalIgnoreCase)))
        {
            var (ok, msg) = reg ? StartupManager.Enable(Environment.ProcessPath ?? "") : StartupManager.Disable();
            Log.Write($"[Setup] startup {(reg ? "on" : "off")}: {ok} {msg}");
            if (reg && ok)
            {
                var c = AppConfig.Load();
                c.StartWithWindows = true;
                c.Save();
            }
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(await SelfTestAsync());
            return;
        }

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

        Hub = new LightHub(config);
        Tray = new TrayIcon(Hub, ShowMain, Quit);
        await Hub.StartAsync();
        _ = PcRgb.PcRgbService.DetectAsync(Hub);
        PcRgb.PcRgbService.WatchKeyboard(Hub);

        // Arranque manual abre sempre a janela; com o Windows (--minimized) fica na tray.
        if (!minimized || !config.StartMinimized) ShowMain();
    }

    /// <summary>
    /// Auto-teste da UI (corre no CI): constrói a janela com uma luz Bluetooth e uma
    /// entidade do HA, passa por todas as páginas e sai com 0 (ok) ou 1 (exceção).
    /// Usa uma pasta de dados temporária, nunca a do utilizador.
    /// </summary>
    private async Task<int> SelfTestAsync()
    {
        AppPaths.DataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Pine3LightControl-selftest");
        try { System.IO.File.Delete(AppPaths.LogFile); } catch { }
        Exception? uiError = null;
        DispatcherUnhandledException += (_, ex) => { uiError ??= ex.Exception; ex.Handled = true; };
        try
        {
            Log.Write($"[SelfTest] {typeof(App).Assembly.GetName().Version}");
            Dpapi.Install();
            var config = new AppConfig();
            config.HomeAssistant.Entities.Add("light.test");
            // OpenRGB sem servidor: o cartão aparece como "offline" e a app não pode falhar por isso
            config.OpenRgb.Enabled = true;
            config.OpenRgb.Devices.Add("Test GPU");
            config.PcRgb.Gpu = true;
            config.PcRgb.Memory = true;
            config.Scenes.Add(new Core.Scenes.Scene
            {
                Name = "Test scene",
                Lights = { new Core.Scenes.SceneLightState { LightId = "ha:light.test", Name = "Test", On = true, Brightness = 128 } },
            });
            config.HomeAssistant.Token = "token";
            if (config.HomeAssistant.Token != "token") throw new Exception("DPAPI round-trip failed");
            config.HomeAssistant.Token = "";
            Appearance.Apply(config.Appearance);

            Hub = new LightHub(config);
            await Hub.StartAsync();
            // Deteção real do hardware (no CI não há nada: tem de falhar com elegância)
            await PcRgb.PcRgbService.DetectAsync(Hub);
            Log.Write($"[SelfTest] gráfica: {PcRgb.PcRgbService.GpuStatus}; memória: {PcRgb.PcRgbService.MemoryStatus}");
            Tray = new TrayIcon(Hub, () => { }, () => { });
            _main = new MainWindow(Hub);
            _main.Show();
            foreach (var tag in _main.PageTags.ToList())
            {
                _main.Go(tag);
                await Task.Delay(300);
                Log.Write($"[SelfTest] página {tag} ok");
            }
            foreach (var l in Hub.Lights) { l.SetPower(true); l.SetBrightness(100); }
            foreach (var sc in Hub.Scenes) Hub.ActivateScene(sc);
            await Task.Delay(300);
            _main.CloseForQuit();
            Tray.Dispose();
            await Hub.DisposeAsync();
            if (uiError is not null) throw uiError;
            Log.Write("[SelfTest] OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"[SelfTest] FALHOU: {ex}");
            return 1;
        }
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
