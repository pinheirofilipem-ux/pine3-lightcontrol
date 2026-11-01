using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.PcRgb;

/// <summary>
/// Ratos Logitech com HID++ 2.0 e a funcionalidade RGB 0x8070 (ex.: G903), pelo
/// recetor LIGHTSPEED (046D:C539, interface 2, coleção dos relatórios longos de 20
/// bytes, report ID 0x11). Protocolo do OpenRGB (LogitechProtocolCommon.cpp,
/// release_1.1): procura a funcionalidade, lê zonas e efeitos e aplica com
/// SET_EFFECT (0x30). Não grava nada no rato.
/// </summary>
public static class LogitechHidpp
{
    public const ushort Vid = 0x046D;
    public const ushort ReceiverPid = 0xC539;
    public const int LongLen = 20;
    public const byte LongReport = 0x11;
    public const ushort FeatureRgbEffects = 0x8070;
    public const byte CmdGetFeature = 0x01;     // root (índice 0), função 0
    public const byte CmdGetCount = 0x00, CmdGetInfo = 0x10, CmdGetControl = 0x20, CmdSetEffect = 0x30;
    public const ushort FxOff = 0x0000, FxOn = 0x0001, FxSpectrum = 0x0003, FxBreathing = 0x000A;
    public const int TimeoutMs = 300;

    public static byte[] Request(byte device, byte feature, byte command, params byte[] data)
    {
        var b = new byte[LongLen];
        b[0] = LongReport; b[1] = device; b[2] = feature; b[3] = command;
        data.CopyTo(b, 4);
        return b;
    }

    /// <summary>Envia e espera a resposta com o mesmo dispositivo/funcionalidade/comando.</summary>
    public static byte[]? Transact(IHidLink hid, byte[] request)
    {
        hid.Write(request);
        for (int i = 0; i < 6; i++)
        {
            var r = hid.Read(TimeoutMs);
            if (r is null) return null;
            if (r.Length >= 4 && r[0] == LongReport && r[1] == request[1] && r[2] == request[2] && r[3] == request[3])
                return r;
            if (r.Length >= 4 && r[1] == request[1] && r[2] == 0xFF) return null;   // erro HID++ 2.0
        }
        return null;
    }

    public sealed record Zone(byte Index, ushort Location, Dictionary<ushort, byte> Effects);

    /// <summary>Procura o rato (índice 1–6 no recetor) com a funcionalidade 0x8070.</summary>
    public static (byte Device, byte Feature, List<Zone> Zones)? Detect(IHidLink hid)
    {
        for (byte dev = 1; dev <= 6; dev++)
        {
            var r = Transact(hid, Request(dev, 0x00, CmdGetFeature, FeatureRgbEffects >> 8, FeatureRgbEffects & 0xFF));
            if (r is null || r[4] == 0) continue;
            byte feature = r[4];
            var count = Transact(hid, Request(dev, feature, CmdGetCount));
            if (count is null) continue;
            var zones = new List<Zone>();
            for (byte z = 0; z < count[4]; z++)
            {
                var info = Transact(hid, Request(dev, feature, CmdGetInfo, z));
                if (info is null) continue;
                var fx = new Dictionary<ushort, byte>();
                for (byte f = 0; f < info[7]; f++)
                {
                    var ctl = Transact(hid, Request(dev, feature, CmdGetControl, z, f));
                    if (ctl is null) continue;
                    ushort type = (ushort)((ctl[6] << 8) | ctl[7]);
                    fx.TryAdd(type, f);
                }
                zones.Add(new Zone(info[4], (ushort)((info[5] << 8) | info[6]), fx));
            }
            if (zones.Count > 0) return (dev, feature, zones);
        }
        return null;
    }

    /// <summary>Pacote SET_EFFECT como logitech_device::setMode (fp8070, sem persistência).</summary>
    public static byte[] SetEffect(byte device, byte feature, byte zone, byte fxIndex, ushort fxType,
        RgbColor c, ushort periodMs, byte brightness)
    {
        var d = new byte[16];
        d[0] = zone; d[1] = fxIndex; d[2] = c.R; d[3] = c.G; d[4] = c.B;
        switch (fxType)
        {
            case FxSpectrum:
                d[7] = (byte)(periodMs >> 8); d[8] = (byte)periodMs; d[9] = brightness;
                break;
            case FxBreathing:
                d[5] = (byte)(periodMs >> 8); d[6] = (byte)periodMs; d[8] = brightness;
                break;
        }
        d[12] = 0x00;
        return Request(device, feature, CmdSetEffect, d);
    }
}

/// <summary>O rato como uma luz: on/off, brilho, cor e 3 efeitos, em todas as zonas.</summary>
public sealed class LogitechMouseLight : LightBase, IDisposable
{
    private static readonly string[] EffectNames = { "Static", "Breathing", "Color cycle" };
    private readonly IHidLink _hid;
    private readonly byte _device, _feature;
    private readonly IReadOnlyList<LogitechHidpp.Zone> _zones;
    private readonly SendQueue _queue = new(TimeSpan.FromMilliseconds(40));
    private string _status;

    public LogitechMouseLight(IHidLink hid, byte device, byte feature, IReadOnlyList<LogitechHidpp.Zone> zones, string name = "Mouse")
    {
        _hid = hid;
        _device = device;
        _feature = feature;
        _zones = zones;
        Name = name;
        IsOn = true;
        Effect = 0;
        Color = new RgbColor(0, 120, 255);
        _status = $"{zones.Count} zone(s)";
        _queue.Error += e =>
        {
            _status = "Error: " + e.Message;
            Log.Write($"[Mouse] {e.Message}");
            RaiseChanged();
        };
    }

    public override string Id => "pc:mouse";
    public override string Name { get; }
    public override LightSource Source => LightSource.Pc;
    public override LightFeatures Features =>
        LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color | LightFeatures.Effects;
    public override bool IsAvailable => true;
    public override string StatusText => _status;
    public override IReadOnlyList<string> Effects => EffectNames;

    private void Push()
    {
        ushort type = !IsOn ? LogitechHidpp.FxOff : Effect switch
        {
            1 => LogitechHidpp.FxBreathing,
            2 => LogitechHidpp.FxSpectrum,
            _ => LogitechHidpp.FxOn,
        };
        // O modo "ligado" não tem brilho: escala-se a cor; os efeitos usam brilho em %
        var c = type == LogitechHidpp.FxOn
            ? new RgbColor((byte)(Color.R * Brightness / 255), (byte)(Color.G * Brightness / 255), (byte)(Color.B * Brightness / 255))
            : Color;
        byte pct = (byte)Math.Clamp((int)Math.Round(Brightness * 100.0 / 255), 1, 100);
        _queue.Post("fx", () =>
        {
            int missing = 0;
            foreach (var z in _zones)
            {
                if (!z.Effects.TryGetValue(type, out var fx))
                {
                    missing++;
                    continue;
                }
                var r = LogitechHidpp.Transact(_hid, LogitechHidpp.SetEffect(_device, _feature, z.Index, fx, type, c, 3000, pct));
                if (r is null) missing++;
            }
            _status = missing == 0 ? $"{_zones.Count} zone(s)" : "Mouse did not answer (asleep or G HUB open?)";
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
