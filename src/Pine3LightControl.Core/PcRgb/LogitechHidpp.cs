using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.PcRgb;

/// <summary>
/// Ratos Logitech com HID++ 2.0 e uma das funcionalidades RGB 0x8070 (RGB_EFFECTS1) ou
/// 0x8071 (RGB_EFFECTS2, ex.: G903 HERO), pelo recetor LIGHTSPEED (046D:C539,
/// interface 2, coleção dos relatórios longos de 20 bytes, report ID 0x11). Protocolo
/// do OpenRGB (LogitechProtocolCommon.cpp): procura a funcionalidade, lê zonas e
/// efeitos e aplica com SET_EFFECT (0x30 na 8070, 0x10 na 8071).
/// </summary>
public static class LogitechHidpp
{
    public const ushort Vid = 0x046D;
    public const ushort ReceiverPid = 0xC539;
    public const int LongLen = 20;
    public const byte LongReport = 0x11;
    public const ushort FeatureRgbEffects = 0x8070, FeatureRgbEffects2 = 0x8071, FeatureSet = 0x0001;
    public const byte CmdGetFeature = 0x01;     // root (índice 0), função 0
    public const byte CmdGetCount = 0x00, CmdGetInfo = 0x10, CmdGetControl = 0x20, CmdSetEffect = 0x30;
    // 0x8071: INFO 0x00 (zonas e efeitos), SET_LED_EFFECT 0x10, CONTROL 0x50
    public const byte Cmd2Info = 0x00, Cmd2SetEffect = 0x10, Cmd2Control = 0x50;
    // IFeatureSet (0x0001): GET_COUNT (função 0) e GET_FEATURE_ID (função 1)
    public const byte CmdFeatureCount = 0x01, CmdFeatureId = 0x11;
    public const ushort FxOff = 0x0000, FxOn = 0x0001, FxSpectrum = 0x0003, FxBreathing = 0x000A;
    public const int TimeoutMs = 500;

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
            if (r.Length >= 4 && r[0] == 0x10 && r[1] == request[1] && r[2] == 0x8F) return null;   // erro HID++ 1.0 (coleção curta)
        }
        return null;
    }

    public sealed record Zone(byte Index, ushort Location, Dictionary<ushort, byte> Effects);

    /// <summary>Índice da funcionalidade <paramref name="page"/> no dispositivo (0 = não tem; null = sem resposta).</summary>
    public static byte? FeatureIndex(IHidLink hid, byte dev, ushort page)
    {
        var r = Transact(hid, Request(dev, 0x00, CmdGetFeature, (byte)(page >> 8), (byte)page));
        return r?[4];
    }

    /// <summary>Lista das funcionalidades do dispositivo (para o log, quando não há RGB conhecido).</summary>
    public static List<ushort> Features(IHidLink hid, byte dev)
    {
        var list = new List<ushort>();
        if (FeatureIndex(hid, dev, FeatureSet) is not { } fs || fs == 0) return list;
        var count = Transact(hid, Request(dev, fs, CmdFeatureCount));
        if (count is null) return list;
        for (byte i = 1; i <= count[4]; i++)
        {
            var r = Transact(hid, Request(dev, fs, CmdFeatureId, i));
            if (r is null) break;
            list.Add((ushort)((r[4] << 8) | r[5]));
        }
        return list;
    }

    /// <summary>
    /// Procura o rato (índice 1–6 no recetor) com a funcionalidade 0x8070 ou 0x8071.
    /// <paramref name="note"/> explica a falha quando um dispositivo respondeu sem RGB conhecido.
    /// </summary>
    public static (byte Device, byte Feature, List<Zone> Zones, ushort Page)? Detect(IHidLink hid, out string? note)
    {
        note = null;
        for (byte dev = 1; dev <= 6; dev++)
        {
            var f1 = FeatureIndex(hid, dev, FeatureRgbEffects);
            if (f1 is null) continue;                      // nada neste índice
            if (f1 > 0 && ReadZones8070(hid, dev, f1.Value) is { Count: > 0 } z1)
                return (dev, f1.Value, z1, FeatureRgbEffects);
            var f2 = FeatureIndex(hid, dev, FeatureRgbEffects2);
            if (f2 > 0 && ReadZones8071(hid, dev, f2.Value) is { Count: > 0 } z2)
                return (dev, f2.Value, z2, FeatureRgbEffects2);
            var pages = Features(hid, dev);
            note = $"device {dev} answered without RGB (features: {string.Join(" ", pages.Select(p => p.ToString("X4")))})";
        }
        return null;
    }

    public static (byte Device, byte Feature, List<Zone> Zones, ushort Page)? Detect(IHidLink hid) => Detect(hid, out _);

    private static List<Zone> ReadZones8070(IHidLink hid, byte dev, byte feature)
    {
        var zones = new List<Zone>();
        var count = Transact(hid, Request(dev, feature, CmdGetCount));
        if (count is null) return zones;
        for (byte z = 0; z < count[4]; z++)
        {
            var info = Transact(hid, Request(dev, feature, CmdGetInfo, z));
            if (info is null) continue;
            var fx = new Dictionary<ushort, byte>();
            for (byte f = 0; f < info[7]; f++)
            {
                var ctl = Transact(hid, Request(dev, feature, CmdGetControl, z, f));
                if (ctl is null) continue;
                fx.TryAdd((ushort)((ctl[6] << 8) | ctl[7]), f);
            }
            zones.Add(new Zone(info[4], (ushort)((info[5] << 8) | info[6]), fx));
        }
        return zones;
    }

    /// <summary>
    /// 0x8071 (como logitech_device::getRGBconfig): INFO com FF FF dá o número de zonas
    /// (data[1..2]); INFO (zona, FF) dá localização data[2..3] e nº de efeitos data[4];
    /// INFO (zona, efeito) dá o tipo em data[2..3].
    /// </summary>
    private static List<Zone> ReadZones8071(IHidLink hid, byte dev, byte feature)
    {
        var zones = new List<Zone>();
        var count = Transact(hid, Request(dev, feature, Cmd2Info, 0xFF, 0xFF));
        if (count is null) return zones;
        int n = (count[5] << 8) | count[6];
        for (int z = 0; z < Math.Min(n, 16); z++)
        {
            var info = Transact(hid, Request(dev, feature, Cmd2Info, (byte)z, 0xFF));
            if (info is null) continue;
            var fx = new Dictionary<ushort, byte>();
            for (byte f = 0; f < info[8]; f++)
            {
                var e = Transact(hid, Request(dev, feature, Cmd2Info, (byte)z, f));
                if (e is null) continue;
                fx.TryAdd((ushort)((e[6] << 8) | e[7]), f);
            }
            zones.Add(new Zone(info[4], (ushort)((info[6] << 8) | info[7]), fx));
        }
        return zones;
    }

    /// <summary>
    /// 0x8071 CONTROL (01 03 05, como set8071Effects): tira o RGB e a gestão de energia
    /// ao firmware para os efeitos enviados ficarem. Volta ao firmware quando o rato desliga.
    /// </summary>
    public static byte[] SoftwareControl8071(byte device, byte feature) =>
        Request(device, feature, Cmd2Control, 0x01, 0x03, 0x05);

    /// <summary>
    /// Pacote SET_EFFECT como logitech_device::setMode. Na 8070 vai sem persistência
    /// (data[12] = 0); na 8071 o OpenRGB usa data[12] = 1 e comando 0x10.
    /// </summary>
    public static byte[] SetEffect(byte device, byte feature, byte zone, byte fxIndex, ushort fxType,
        RgbColor c, ushort periodMs, byte brightness, ushort page = FeatureRgbEffects)
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
        bool v1 = page == FeatureRgbEffects;
        d[12] = v1 ? (byte)0x00 : (byte)0x01;
        return Request(device, feature, v1 ? CmdSetEffect : Cmd2SetEffect, d);
    }
}

/// <summary>O rato como uma luz: on/off, brilho, cor e 3 efeitos, em todas as zonas.</summary>
public sealed class LogitechMouseLight : LightBase, IDisposable
{
    private static readonly string[] EffectNames = { "Static", "Breathing", "Color cycle" };
    private readonly IHidLink _hid;
    private readonly byte _device, _feature;
    private readonly ushort _page;
    private bool _controlSent;
    private readonly IReadOnlyList<LogitechHidpp.Zone> _zones;
    private readonly SendQueue _queue = new(TimeSpan.FromMilliseconds(40));
    private string _status;

    public LogitechMouseLight(IHidLink hid, byte device, byte feature, IReadOnlyList<LogitechHidpp.Zone> zones,
        string name = "Mouse", ushort page = LogitechHidpp.FeatureRgbEffects)
    {
        _page = page;
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
            if (_page == LogitechHidpp.FeatureRgbEffects2 && !_controlSent)
                _controlSent = LogitechHidpp.Transact(_hid, LogitechHidpp.SoftwareControl8071(_device, _feature)) is not null;
            foreach (var z in _zones)
            {
                if (!z.Effects.TryGetValue(type, out var fx))
                {
                    missing++;
                    continue;
                }
                var r = LogitechHidpp.Transact(_hid, LogitechHidpp.SetEffect(_device, _feature, z.Index, fx, type, c, 3000, pct, _page));
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
