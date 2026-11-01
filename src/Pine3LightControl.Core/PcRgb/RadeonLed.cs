using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.PcRgb;

/// <summary>
/// LED das Radeon RX 6000 de referência: controlador USB da Cooler Master
/// (VID 0x2516, PID 0x014D — RX 6800/6800 XT/6950 XT —, ou 0x015B — RX 6900 XT —,
/// interface HID 1). Protocolo do OpenRGB (Controllers/CoolerMasterController/
/// CMR6000Controller, release_1.1). Relatórios de 65 bytes.
/// </summary>
public static class Cmr6000
{
    public const ushort Vid = 0x2516;
    public const ushort Pid6000 = 0x014D;
    public const ushort Pid6900 = 0x015B;
    public const int ReportSize = 65;
    public const int ReadTimeoutMs = 250;

    public const byte ModeDirect = 0x00;
    public const byte ModeBreathe = 0x01;
    public const byte ModeColorCycle = 0x02;
    public const byte ModeOff = 0xFF;

    public const byte SpeedDirect = 0xFF;
    public const byte SpeedBreatheNormal = 0x31;
    public const byte SpeedCycleNormal = 0x80;

    private static byte[] Report(params byte[] start)
    {
        var b = new byte[ReportSize];
        start.CopyTo(b, 0);
        return b;
    }

    public static byte[] Off() => Report(0x00, 0x41, 0x43);
    public static byte[] Enable() => Report(0x00, 0x41, 0x80);
    public static byte[] Apply() => Report(0x00, 0x51, 0x28, 0x00, 0x00, 0xE0);

    public static byte[] SecondColour(RgbColor c1, RgbColor c2) =>
        Report(0x00, 0x51, 0x9C, 0x01, 0x00, c1.R, c1.G, c1.B, c2.R, c2.G, c2.B);

    public static byte[] Effect(byte mode, byte speed, byte random, byte brightness, RgbColor c)
    {
        var b = new byte[ReportSize];
        Array.Fill(b, (byte)0xFF);
        b[0x00] = 0x00;
        b[0x01] = 0x51;
        b[0x02] = 0x2C;
        b[0x03] = 0x01;
        b[0x04] = 0x00;
        b[0x05] = mode;
        b[0x06] = speed;
        b[0x07] = random;
        b[0x08] = mode switch { ModeBreathe => 0x03, _ => 0xFF };
        b[0x0A] = brightness;
        bool cycle = mode == ModeColorCycle;
        b[0x0B] = cycle ? (byte)0xFF : c.R;
        b[0x0C] = cycle ? (byte)0xFF : c.G;
        b[0x0D] = cycle ? (byte)0xFF : c.B;
        b[0x0E] = 0x00;
        b[0x0F] = 0x00;
        b[0x10] = 0x00;
        return b;
    }

    public static byte[] ColourConfig(byte mode)
    {
        var b = Report(0x00, 0x51, 0xA0, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x05, 0x06);
        for (int i = 0x0B; i < 0x1A; i++) b[i] = mode;
        return b;
    }

    /// <summary>Sequência completa, como CMR6000Controller::SendUpdate.</summary>
    public static void Send(IHidLink hid, ushort pid, byte mode, byte speed, byte brightness, RgbColor color)
    {
        if (mode == ModeOff)
        {
            hid.Write(Off());
            return;
        }
        hid.Write(Enable());
        hid.Read(ReadTimeoutMs);
        if (pid == Pid6900)
        {
            hid.Write(SecondColour(color, new RgbColor(0, 0, 0)));
            hid.Read(ReadTimeoutMs);
        }
        hid.Write(Effect(mode, speed, 0x00, brightness, color));
        hid.Write(ColourConfig(mode));
        hid.Read(ReadTimeoutMs);
        hid.Write(Apply());
        hid.Read(ReadTimeoutMs);
    }
}

/// <summary>O logótipo Radeon como luz: on/off, brilho, cor e 3 efeitos.</summary>
public sealed class RadeonLedLight : LightBase, IDisposable
{
    private static readonly string[] EffectNames = { "Static", "Breathing", "Color cycle" };
    private readonly IHidLink _hid;
    private readonly ushort _pid;
    private readonly SendQueue _queue = new(TimeSpan.FromMilliseconds(30));
    private string _status = "Ready";

    public RadeonLedLight(IHidLink hid, ushort pid, string name = "Radeon logo")
    {
        _hid = hid;
        _pid = pid;
        Name = name;
        Effect = 0;
        IsOn = true;
        Color = new RgbColor(255, 0, 0);
        _queue.Error += e =>
        {
            _status = "Error: " + e.Message;
            Log.Write($"[GPU] {e.Message}");
            RaiseChanged();
        };
    }

    public override string Id => "pc:gpu";
    public override string Name { get; }
    public override LightSource Source => LightSource.Pc;
    public override LightFeatures Features =>
        LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color | LightFeatures.Effects;
    public override bool IsAvailable => true;
    public override string StatusText => _status;
    public override IReadOnlyList<string> Effects => EffectNames;

    private void Push()
    {
        byte mode = !IsOn ? Cmr6000.ModeOff : Effect switch
        {
            1 => Cmr6000.ModeBreathe,
            2 => Cmr6000.ModeColorCycle,
            _ => Cmr6000.ModeDirect,
        };
        byte speed = mode switch
        {
            Cmr6000.ModeBreathe => Cmr6000.SpeedBreatheNormal,
            Cmr6000.ModeColorCycle => Cmr6000.SpeedCycleNormal,
            _ => Cmr6000.SpeedDirect,
        };
        var (bri, color) = ((byte)Brightness, Color);
        _queue.Post("led", () =>
        {
            Cmr6000.Send(_hid, _pid, mode, speed, bri, color);
            _status = "Ready";
            return Task.CompletedTask;
        });
    }

    public override void SetPower(bool on) { IsOn = on; Push(); RaiseChanged(); }
    public override void SetBrightness(int v) { Brightness = Math.Clamp(v, 1, 255); IsOn = true; Push(); RaiseChanged(); }
    public override void SetColor(RgbColor c) { Color = c; IsOn = true; if (Effect == 2) Effect = 0; Push(); RaiseChanged(); }

    public override void SetEffect(int index)
    {
        if (index < 0 || index >= EffectNames.Length) return;
        Effect = index;
        IsOn = true;
        Push();
        RaiseChanged();
    }

    public Task WhenSent() => _queue.WhenIdle();
    public void Dispose() => _hid.Dispose();
}
