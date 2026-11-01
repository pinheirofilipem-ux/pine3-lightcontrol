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
