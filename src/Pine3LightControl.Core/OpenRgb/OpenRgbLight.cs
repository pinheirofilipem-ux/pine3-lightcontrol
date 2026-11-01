using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.OpenRgb;

/// <summary>
/// Um grupo de dispositivos do OpenRGB com o mesmo nome (ex.: as 4 memórias)
/// visto como uma só luz: ligar/desligar, brilho e cor.
/// O brilho é aplicado escalando a cor (o modo Direct não tem brilho próprio).
/// </summary>
public sealed class OpenRgbLight : LightBase
{
    private readonly OpenRgbClient _client;
    private readonly SendQueue _queue = new(TimeSpan.FromMilliseconds(40));
    private List<OpenRgbDevice> _devices = new();

    public OpenRgbLight(OpenRgbClient client, string groupName)
    {
        _client = client;
        GroupName = groupName;
        _queue.Error += e => Log.Write($"[OpenRGB] {GroupName}: {e.Message}");
        Brightness = 255;
        Color = RgbColor.White;
    }

    public string GroupName { get; }
    public bool HasDevices => _devices.Count > 0;
    public override string Id => "orgb:" + GroupName;
    public override string Name => _devices.Count > 1 ? $"{GroupName} ×{_devices.Count}" : GroupName;
    public override LightSource Source => LightSource.OpenRgb;
    public override LightFeatures Features => LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color;
    public override bool IsAvailable => _client.State == OpenRgbState.Connected && _devices.Count > 0;
    public string TypeName => _devices.FirstOrDefault()?.TypeName ?? "";

    public override string StatusText =>
        _client.State != OpenRgbState.Connected ? "OpenRGB offline" :
        _devices.Count == 0 ? "Not found in OpenRGB" :
        TypeName;

    /// <summary>
    /// Novos dispositivos (ligação nova ou rescan). Na primeira vez lê a cor atual
    /// dos LEDs; depois mantém o estado escolhido na app.
    /// </summary>
    public void Update(IEnumerable<OpenRgbDevice> devices, bool readState)
    {
        _devices = devices.ToList();
        if (readState && _devices.Count > 0)
        {
            var lit = _devices.SelectMany(d => d.Colors).Where(c => c.R + c.G + c.B > 0).ToList();
            IsOn = lit.Count > 0;
            if (IsOn)
            {
                var c = lit[0];
                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                // Separa cor e brilho: cor normalizada, brilho = componente mais forte
                Brightness = max;
                Color = new RgbColor((byte)(c.R * 255 / max), (byte)(c.G * 255 / max), (byte)(c.B * 255 / max));
            }
        }
        RaiseChanged();
    }

    public void ConnectionChanged() => RaiseChanged();

    private void Push()
    {
        var (r, g, b) = IsOn
            ? ((byte)(Color.R * Brightness / 255), (byte)(Color.G * Brightness / 255), (byte)(Color.B * Brightness / 255))
            : ((byte)0, (byte)0, (byte)0);
        var targets = _devices.ToList();
        _queue.Post("leds", async () =>
        {
            foreach (var d in targets) await _client.SetColorAsync(d.Index, d.LedCount, r, g, b).ConfigureAwait(false);
        });
    }

    public override void SetPower(bool on)
    {
        IsOn = on;
        Push();
        RaiseChanged();
    }

    public override void SetBrightness(int value)
    {
        Brightness = Math.Clamp(value, 1, 255);
        IsOn = true;
        Push();
        RaiseChanged();
    }

    public override void SetColor(RgbColor color)
    {
        Color = color;
        IsOn = true;
        Push();
        RaiseChanged();
    }

    public Task WhenSent() => _queue.WhenIdle();
}
