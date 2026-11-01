using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pine3LightControl.Core;
using Pine3LightControl.Core.Lights;
using Pine3LightControl.Core.PcRgb;

namespace Pine3LightControl.PcRgb;

/// <summary>Deteta o RGB do PC (gráfica e memórias) e entrega as luzes ao hub.</summary>
internal static class PcRgbService
{
    public static string GpuStatus { get; private set; } = "Not checked";
    public static string MemoryStatus { get; private set; } = "Off";
    public static string MouseStatus { get; private set; } = "Off";
    public static string KeyboardStatus { get; private set; } = "Off";

    public static Task DetectAsync(LightHub hub) => Task.Run(() =>
    {
        lock (DetectGate) Detect(hub);
    });

    private static void Detect(LightHub hub)
    {
        var cfg = hub.Config.PcRgb;
        var lights = new List<ILight>();

        if (cfg.Gpu)
        {
            var hid = HidSharpLink.OpenRadeon(out var pid, out var problem);
            if (hid is null) GpuStatus = $"Not found — {problem}";
            else
            {
                lights.Add(new RadeonLedLight(hid, pid));
                GpuStatus = $"Radeon LED controller found (2516:{pid:X4})";
            }
        }
        else GpuStatus = "Off";

        if (cfg.Memory)
        {
            var bus = PawnIoSmbus.Open(out var problem);
            if (bus is null) MemoryStatus = $"Not available — {problem}";
            else
            {
                var modules = CorsairDram.Detect(bus, m => Log.Write($"[RAM] {m}"));
                if (modules.Count == 0)
                {
                    bus.Dispose();
                    MemoryStatus = "No Corsair Vengeance RGB Pro DDR4 modules found";
                }
                else
                {
                    lights.Add(new CorsairRamLight(bus, modules));
                    MemoryStatus = $"{modules.Count} module(s) found at " +
                                   string.Join(", ", modules.ConvertAll(m => $"0x{m.Address:X2}"));
                }
            }
        }
        else MemoryStatus = "Off";

        if (cfg.Mouse)
        {
            // Recetor LIGHTSPEED: coleção HID++ dos relatórios longos (20 bytes)
            var raw = HidSharpLink.Open(LogitechHidpp.Vid,
                d => d.ProductID == LogitechHidpp.ReceiverPid && d.GetMaxOutputReportLength() == LogitechHidpp.LongLen,
                "Logitech LIGHTSPEED receiver", out var dev, out var problem);
            if (raw is null) MouseStatus = $"Not found — {problem}";
            else
            {
                Log.Write($"[Mouse] recetor (longo): {HidSharpLink.Describe(dev!)}");
                // Coleção curta (7 bytes) da mesma interface: é por lá que chegam os erros
                var shortDev = HidSharpLink.FindAll(LogitechHidpp.Vid, d => d.ProductID == LogitechHidpp.ReceiverPid &&
                    d.GetMaxOutputReportLength() == 7).FirstOrDefault();
                var shortLink = shortDev is null ? null : HidSharpLink.TryOpen(shortDev);
                if (shortDev is not null) Log.Write($"[Mouse] recetor (curto): {HidSharpLink.Describe(shortDev)} aberto={shortLink is not null}");
                var link = new PairedHid(raw, shortLink);
                var found = LogitechHidpp.Detect(new LoggingHid(link, "Mouse"), out var note);
                if (found is null)
                {
                    link.Dispose();
                    if (note is not null) Log.Write($"[Mouse] {note}");
                    MouseStatus = note is not null
                        ? $"Mouse answered, but without a known RGB feature ({note})"
                        : "Receiver found, but the mouse didn't answer — move it (to wake it up) and press Save and detect again. Close G HUB.";
                }
                else
                {
                    var (idx, feature, zones, page) = found.Value;
                    Log.Write($"[Mouse] RGB {page:X4} no índice {feature}, {zones.Count} zona(s): " +
                        string.Join("; ", zones.Select(z => $"zona {z.Index} loc {z.Location:X4} efeitos {string.Join(",", z.Effects.Keys.Select(k => k.ToString("X")))}")));
                    // Regista os primeiros pacotes trocados depois da deteção (diagnóstico)
                    lights.Add(new LogitechMouseLight(new LoggingHid(link, "Mouse") { Limit = 40 }, idx, feature, zones, page: page));
                    MouseStatus = $"Logitech mouse found ({zones.Count} zone(s), receiver slot {idx}, {page:X4})";
                }
            }
        }
        else MouseStatus = "Off";

        if (cfg.Keyboard) lights.AddRange(DetectKeyboard());
        else KeyboardStatus = "Off";

        Log.Write($"[PC RGB] gráfica: {GpuStatus}; memória: {MemoryStatus}; rato: {MouseStatus}; teclado: {KeyboardStatus}");
        hub.SetPcLights(lights);
    }

    /// <summary>Procura o teclado (só com fio) e devolve as zonas encontradas.</summary>
    private static List<ILight> DetectKeyboard()
    {
        var found = new List<ILight>();
        // O OK-M tem várias interfaces "vendor-defined": experimenta cada uma até o VIA responder.
        // Só com fio (28E9:31C6): o recetor 2.4 GHz (056D:C077) não passa o VIA (confirmado na 0.5.4).
        var candidates = HidSharpLink.FindAll(DuckyOkM.Vid, d => d.ProductID == DuckyOkM.Pid &&
            (HidSharpLink.HasUsage(d, 0xFF60, 0x61) || d.GetMaxOutputReportLength() is 33 or 65))
            .OrderByDescending(c => HidSharpLink.HasUsage(c, 0xFF60, 0x61)).ToList();
        bool dongle = HidSharpLink.FindAll(DuckyOkM.DongleVid, d => d.ProductID == DuckyOkM.DonglePid).Count > 0;
        if (candidates.Count == 0)
            KeyboardStatus = dongle
                ? "Keyboard is on the wireless receiver — lighting only works with the USB cable"
                : "Not found — connect the keyboard with the USB cable";
        else
        {
            KeyboardStatus = "Found, but no interface answered the VIA protocol (close the VIA app)";
            foreach (var d in candidates)
            {
                Log.Write($"[Keyboard] a experimentar {HidSharpLink.Describe(d)}");
                var raw = HidSharpLink.TryOpen(d);
                if (raw is null) { Log.Write("[Keyboard] não abriu"); continue; }
                var zones = DuckyOkM.Create(new LoggingHid(raw, "Keyboard") { Limit = 20 }, out var p2);
                if (zones is null)
                {
                    Log.Write($"[Keyboard] {p2}");
                    raw.Dispose();
                    continue;
                }
                found.AddRange(zones);
                KeyboardStatus = "Ducky OK-M 75 found (keys + side light)";
                break;
            }
        }
        return found;
    }

    private static readonly object DetectGate = new();
    private static System.Threading.Timer? _plugTimer;

    /// <summary>
    /// O teclado só funciona com o cabo: quando é ligado ou desligado, deteta-o outra vez
    /// (só o teclado: a gráfica, as memórias e o rato ficam como estão) e o hub repõe-lhe
    /// o último estado guardado.
    /// </summary>
    public static void WatchKeyboard(LightHub hub)
    {
        _plugTimer = new System.Threading.Timer(_ => CheckKeyboard(hub));
        HidSharp.DeviceList.Local.Changed += (_, _) =>
            _plugTimer.Change(TimeSpan.FromSeconds(2), System.Threading.Timeout.InfiniteTimeSpan);
    }

    private static void CheckKeyboard(LightHub hub)
    {
        try
        {
            if (!hub.Config.PcRgb.Keyboard) return;
            lock (DetectGate)
            {
                bool present = HidSharpLink.FindAll(DuckyOkM.Vid, d => d.ProductID == DuckyOkM.Pid).Count > 0;
                var current = hub.PcLights.Where(l => l.Id.StartsWith("pc:keyboard", StringComparison.Ordinal)).ToList();
                if (present == current.Count > 0) return;
                Log.Write($"[Keyboard] {(present ? "ligado" : "desligado")} (cabo)");
                var others = hub.PcLights.Except(current).ToList();
                if (present) others.AddRange(DetectKeyboard());
                else KeyboardStatus = "Not found — connect the keyboard with the USB cable";
                hub.SetPcLights(others);
            }
        }
        catch (Exception e)
        {
            Log.Write($"[Keyboard] deteção automática: {e.Message}");
        }
    }
}
