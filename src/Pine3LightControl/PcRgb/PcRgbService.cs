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
                    lights.Add(new LogitechMouseLight(link, idx, feature, zones, page: page));
                    MouseStatus = $"Logitech mouse found ({zones.Count} zone(s), receiver slot {idx}, {page:X4})";
                }
            }
        }
        else MouseStatus = "Off";

        if (cfg.Keyboard)
        {
            // O OK-M tem várias interfaces "vendor-defined": experimenta cada uma até o VIA responder
            var candidates = HidSharpLink.FindAll(DuckyOkM.Vid, d => d.ProductID == DuckyOkM.Pid &&
                (HidSharpLink.HasUsage(d, 0xFF60, 0x61) || d.GetMaxOutputReportLength() is 33 or 65));
            if (candidates.Count == 0) KeyboardStatus = "Not found — connect the keyboard with the USB cable";
            else
            {
                KeyboardStatus = "Found, but no interface answered the VIA protocol (close the VIA app)";
                foreach (var d in candidates.OrderByDescending(c => HidSharpLink.HasUsage(c, 0xFF60, 0x61)))
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
                    lights.AddRange(zones);
                    KeyboardStatus = "Ducky OK-M 75 found (keys + side light)";
                    break;
                }
            }
        }
        else KeyboardStatus = "Off";

        Log.Write($"[PC RGB] gráfica: {GpuStatus}; memória: {MemoryStatus}; rato: {MouseStatus}; teclado: {KeyboardStatus}");
        hub.SetPcLights(lights);
    });
}
