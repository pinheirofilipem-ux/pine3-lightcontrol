using System;
using System.Collections.Generic;
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

        Log.Write($"[PC RGB] gráfica: {GpuStatus}; memória: {MemoryStatus}");
        hub.SetPcLights(lights);
    });
}
