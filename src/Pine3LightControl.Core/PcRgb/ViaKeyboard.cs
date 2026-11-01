using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.PcRgb;

/// <summary>
/// Iluminação de teclados VIA (protocolo 12, menus "custom"): relatórios raw HID de
/// 32 bytes (+ report ID 0x00 = 33; o OK-M 75 usa 64 + 1, o link completa com zeros),
/// interface de usage page 0xFF60.
/// Comandos: 0x01 versão, 0x07 set value, 0x08 get value (canal, id, dados).
/// Valores: 1 brilho, 2 efeito, 3 velocidade, 4 cor (hue, sat). Nada é gravado
/// na EEPROM (sem 0x09): as alterações duram até o teclado reiniciar.
/// </summary>
public static class Via
{
    public const int ReportSize = 33;
    public const byte CmdProtocolVersion = 0x01;
    public const byte CmdSetValue = 0x07;
    public const byte CmdGetValue = 0x08;
    public const byte CmdUnhandled = 0xFF;
    public const byte ValBrightness = 1, ValEffect = 2, ValSpeed = 3, ValColor = 4;
    public const int ReadTimeoutMs = 300;

    public static byte[] Report(params byte[] payload)
    {
        var b = new byte[ReportSize];
        payload.CopyTo(b, 1);   // b[0] = report ID 0
        return b;
    }

    /// <summary>Envia e espera a resposta ao mesmo comando (o teclado ecoa o pedido).</summary>
    public static byte[]? Transact(IHidLink hid, params byte[] payload)
    {
        hid.Write(Report(payload));
        for (int i = 0; i < 4; i++)
        {
            var r = hid.Read(ReadTimeoutMs);
            if (r is null) return null;
            // Com report ID à frente (Windows: 33 ou 65 bytes, conforme o teclado use
            // relatórios de 32 ou 64) ou sem ele
            var d = r.Length % 2 == 1 && r[0] == 0 ? r.AsSpan(1).ToArray() : r;
            if (d.Length > 0 && (d[0] == payload[0] || d[0] == CmdUnhandled)) return d;
        }
        return null;
    }

    public static int ProtocolVersion(IHidLink hid)
    {
        var r = Transact(hid, CmdProtocolVersion);
        return r is null || r[0] != CmdProtocolVersion ? -1 : (r[1] << 8) | r[2];
    }

    public static byte[]? Get(IHidLink hid, byte channel, byte id)
    {
        var r = Transact(hid, CmdGetValue, channel, id);
        return r is null || r[0] != CmdGetValue ? null : r.AsSpan(3).ToArray();
    }

    public static bool Set(IHidLink hid, byte channel, byte id, params byte[] value)
    {
        var p = new byte[3 + value.Length];
        p[0] = CmdSetValue; p[1] = channel; p[2] = id;
        value.CopyTo(p, 3);
        var r = Transact(hid, p);
        return r is not null && r[0] == CmdSetValue;
    }

    /// <summary>RGB → (hue, sat, val) 0–255, como o HSV do QMK.</summary>
    public static (byte H, byte S, byte V) ToHsv(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        double s = max == 0 ? 0 : d / max;
        return ((byte)Math.Round(h / 360 * 255) , (byte)Math.Round(s * 255), (byte)Math.Round(max * 255));
    }

    public static RgbColor FromHsv(byte h, byte s)
    {
        double hh = h / 255.0 * 360, ss = s / 255.0, v = 1;
        double c = v * ss, x = c * (1 - Math.Abs(hh / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = hh switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new RgbColor(B(r + m), B(g + m), B(b + m));
    }
}

/// <summary>Uma zona de iluminação de um teclado VIA (ex.: teclas ou luz lateral).</summary>
public sealed class ViaZoneLight : LightBase, IDisposable
{
    private readonly IHidLink _hid;
    private readonly object _hidGate;
    private readonly byte _channel;
    private readonly string[] _effects;
    private readonly SendQueue _queue = new(TimeSpan.FromMilliseconds(30));
    private int _lastEffect = 1;
    private string _status = "Ready";

    /// <summary>A zona que fecha a ligação HID (partilhada pelas zonas do mesmo teclado).</summary>
    public bool OwnsHid { get; init; }

    public ViaZoneLight(IHidLink hid, object hidGate, byte channel, string id, string name, string[] effects)
    {
        _hid = hid;
        _hidGate = hidGate;
        _channel = channel;
        Id = id;
        Name = name;
        _effects = effects;
        _queue.Error += e =>
        {
            _status = "Error: " + e.Message;
            Log.Write($"[Keyboard] {e.Message}");
            RaiseChanged();
        };
    }

    public override string Id { get; }
    public override string Name { get; }
    public override LightSource Source => LightSource.Pc;
    public override LightFeatures Features =>
        LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color | LightFeatures.Effects | LightFeatures.Speed;
    public override bool IsAvailable => true;
    public override string StatusText => _status;
    public override IReadOnlyList<string> Effects => _effects;

    /// <summary>Lê o estado atual do teclado.</summary>
    public void ReadState()
    {
        lock (_hidGate)
        {
            if (Via.Get(_hid, _channel, Via.ValBrightness) is { Length: > 0 } b) Brightness = Math.Max((int)b[0], 1);
            if (Via.Get(_hid, _channel, Via.ValEffect) is { Length: > 0 } e)
            {
                Effect = e[0] < _effects.Length ? e[0] : 1;
                IsOn = e[0] != 0;
                if (e[0] != 0) _lastEffect = Effect;
            }
            if (Via.Get(_hid, _channel, Via.ValSpeed) is { Length: > 0 } s) Speed = s[0];
            if (Via.Get(_hid, _channel, Via.ValColor) is { Length: > 1 } c) Color = Via.FromHsv(c[0], c[1]);
        }
        RaiseChanged();
    }

    private void Post(string key, byte id, params byte[] value) =>
        _queue.Post(key, () =>
        {
            bool ok;
            lock (_hidGate) ok = Via.Set(_hid, _channel, id, value);
            _status = ok ? "Ready" : "Keyboard did not answer";
            return Task.CompletedTask;
        });

    public override void SetPower(bool on)
    {
        IsOn = on;
        Effect = on ? _lastEffect : 0;
        Post("effect", Via.ValEffect, (byte)Effect);
        RaiseChanged();
    }

    public override void SetEffect(int index)
    {
        if (index < 0 || index >= _effects.Length) return;
        Effect = index;
        IsOn = index != 0;
        if (index != 0) _lastEffect = index;
        Post("effect", Via.ValEffect, (byte)index);
        RaiseChanged();
    }

    public override void SetBrightness(int value)
    {
        Brightness = Math.Clamp(value, 1, 255);
        Post("brightness", Via.ValBrightness, (byte)Brightness);
        if (!IsOn) SetPower(true);
        else RaiseChanged();
    }

    public override void SetColor(RgbColor color)
    {
        Color = color;
        var (h, s, _) = Via.ToHsv(color);
        Post("color", Via.ValColor, h, s);
        if (!IsOn) SetPower(true);
        else RaiseChanged();
    }

    public override void SetSpeed(int value)
    {
        Speed = Math.Clamp(value, 0, 255);
        Post("speed", Via.ValSpeed, (byte)Speed);
        RaiseChanged();
    }

    public Task WhenSent() => _queue.WhenIdle();

    public void Dispose()
    {
        if (OwnsHid) _hid.Dispose();
    }
}

/// <summary>Ducky OK-M 75 (VID 0x28E9, PID 0x31C6): definição VIA do fabricante (V0105).</summary>
public static class DuckyOkM
{
    public const ushort Vid = 0x28E9;
    public const ushort Pid = 0x31C6;
    /// <summary>Recetor 2.4 GHz do OK-M 75 (identificado no PC do utilizador; não é da Ducky).</summary>
    public const ushort DongleVid = 0x056D, DonglePid = 0xC077;
    public const byte ChannelUnderglow = 2;   // id_qmk_rgblight_*
    public const byte ChannelKeys = 3;        // id_qmk_rgb_matrix_*

    public static readonly string[] KeyEffects =
    {
        "All Off", "Solid Color", "Breathing", "Band Val.", "Cycle All", "Cycle Left/Right", "Cycle Up/Down",
        "Rainbow Moving Chevron", "Cycle Out/In", "Cycle Out/In Dual", "Cycle Pinwheel", "Cycle Spiral",
        "Dual Beacon", "Rainbow Beacon", "Typing Heatmap", "Digital Rain", "Solid Reactive Simple",
        "Solid Reactive Nexus", "Splash", "Solid Splash",
    };

    public static readonly string[] UnderglowEffects =
    {
        "All Off", "Solid Color", "Breathing 1", "Breathing 2", "Breathing 3", "Breathing 4",
        "Rainbow Mood 1", "Rainbow Mood 2", "Rainbow Mood 3", "Rainbow Swirl 1", "Rainbow Swirl 2",
        "Rainbow Swirl 3", "Rainbow Swirl 4", "Rainbow Swirl 5", "Rainbow Swirl 6",
        "Snake 1", "Snake 2", "Snake 3", "Snake 4", "Snake 5", "Snake 6",
    };

    /// <summary>Confirma o protocolo VIA e cria as duas zonas (já com o estado lido).</summary>
    public static IReadOnlyList<ViaZoneLight>? Create(IHidLink hid, out string problem)
    {
        int v = Via.ProtocolVersion(hid);
        if (v < 12)
        {
            problem = v < 0 ? "keyboard did not answer the VIA protocol" : $"VIA protocol {v} not supported (needs 12)";
            return null;
        }
        problem = "";
        var gate = new object();
        var keys = new ViaZoneLight(hid, gate, ChannelKeys, "pc:keyboard-keys", "Keyboard", KeyEffects) { OwnsHid = true };
        var glow = new ViaZoneLight(hid, gate, ChannelUnderglow, "pc:keyboard-glow", "Keyboard side light", UnderglowEffects);
        keys.ReadState();
        glow.ReadState();
        return new[] { keys, glow };
    }
}
