using System;
using System.Linq;
using HidSharp;
using Pine3LightControl.Core.PcRgb;

namespace Pine3LightControl.PcRgb;

/// <summary>Dispositivo HID com HidSharp (relatórios de tamanho fixo).</summary>
internal sealed class HidSharpLink : IHidLink
{
    private readonly HidStream _stream;
    private readonly int _outLen;

    private HidSharpLink(HidStream stream, int outLen)
    {
        _stream = stream;
        _outLen = outLen;
    }

    /// <summary>Abre o primeiro dispositivo HID que cumpra o filtro.</summary>
    public static HidSharpLink? Open(int vid, Func<HidDevice, bool> match, string what, out HidDevice? device, out string problem)
    {
        device = null;
        problem = "";
        try
        {
            device = DeviceList.Local.GetHidDevices(vid).FirstOrDefault(d =>
            {
                try { return match(d); } catch { return false; }
            });
            if (device is null)
            {
                problem = $"no {what} found";
                return null;
            }
            if (!device.TryOpen(out HidStream stream))
            {
                problem = $"could not open the {what} (is another RGB app using it?)";
                return null;
            }
            int len = device.GetMaxOutputReportLength();
            return new HidSharpLink(stream, len);
        }
        catch (Exception e)
        {
            problem = e.Message;
            return null;
        }
    }

    /// <summary>Todos os dispositivos HID que cumpram o filtro (ex.: interfaces de um teclado).</summary>
    public static System.Collections.Generic.List<HidDevice> FindAll(int vid, Func<HidDevice, bool> match)
    {
        try
        {
            return DeviceList.Local.GetHidDevices(vid).Where(d =>
            {
                try { return match(d); } catch { return false; }
            }).ToList();
        }
        catch { return new(); }
    }

    public static HidSharpLink? TryOpen(HidDevice d)
    {
        try
        {
            if (!d.TryOpen(out HidStream stream)) return null;
            return new HidSharpLink(stream, d.GetMaxOutputReportLength());
        }
        catch { return null; }
    }

    /// <summary>Descrição curta para o log (interface, tamanhos, usages).</summary>
    public static string Describe(HidDevice d)
    {
        string usages = "";
        try
        {
            usages = string.Join(",", d.GetReportDescriptor().DeviceItems
                .SelectMany(i => i.Usages.GetAllValues()).Select(u => u.ToString("X8")).Distinct());
        }
        catch { }
        var path = d.DevicePath;
        int mi = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        var iface = mi >= 0 ? path.Substring(mi, Math.Min(12, path.Length - mi)) : "?";
        return $"{iface} out={d.GetMaxOutputReportLength()} in={d.GetMaxInputReportLength()} usages={usages}";
    }

    /// <summary>Tem a usage page/usage indicada no descritor (ex.: 0xFF60/0x61 do VIA).</summary>
    public static bool HasUsage(HidDevice d, uint usagePage, uint usage)
    {
        var target = (usagePage << 16) | usage;
        return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(target));
    }

    /// <summary>Tem alguma usage page do fabricante (0xFF00–0xFFFF): onde podem viver protocolos como o VIA.</summary>
    public static bool HasVendorPage(HidDevice d) =>
        d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => (u >> 16) >= 0xFF00));

    /// <summary>Abre o controlador do LED Radeon (interface HID 1). Null se não existir.</summary>
    public static HidSharpLink? OpenRadeon(out ushort pid, out string problem)
    {
        pid = 0;
        problem = "";
        try
        {
            var dev = DeviceList.Local.GetHidDevices(Cmr6000.Vid)
                .Where(d => d.ProductID is Cmr6000.Pid6000 or Cmr6000.Pid6900)
                .FirstOrDefault(d => d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase));
            if (dev is null)
            {
                problem = "no Radeon RX 6000 reference LED controller found";
                return null;
            }
            if (!dev.TryOpen(out HidStream stream))
            {
                problem = "could not open the LED controller (is the AMD RGB tool or OpenRGB using it?)";
                return null;
            }
            stream.ReadTimeout = Cmr6000.ReadTimeoutMs;
            pid = (ushort)dev.ProductID;
            int len = dev.GetMaxOutputReportLength();
            return new HidSharpLink(stream, len > 0 ? len : Cmr6000.ReportSize);
        }
        catch (Exception e)
        {
            problem = e.Message;
            return null;
        }
    }

    public void Write(byte[] report)
    {
        var buf = report;
        if (buf.Length != _outLen)
        {
            buf = new byte[_outLen];
            Array.Copy(report, buf, Math.Min(report.Length, _outLen));
        }
        _stream.Write(buf, 0, buf.Length);
    }

    public byte[]? Read(int timeoutMs)
    {
        try
        {
            _stream.ReadTimeout = timeoutMs;
            return _stream.Read();
        }
        catch (TimeoutException) { return null; }
        catch (System.IO.IOException) { return null; }
    }

    public void Dispose() => _stream.Dispose();
}
