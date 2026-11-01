using System.Text.Json.Nodes;
using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.HomeAssistant;

/// <summary>Entidade do Home Assistant vista como luz (light, switch, scene, ...).</summary>
public sealed class HaLight : LightBase
{
    private readonly HaClient _client;
    private readonly SendQueue _queue = new(TimeSpan.FromMilliseconds(150));
    private HaEntity? _entity;
    private string _error = "";

    public HaLight(HaClient client, string entityId)
    {
        _client = client;
        EntityId = entityId;
        _queue.Error += e =>
        {
            _error = e.Message;
            Log.Write($"[HA] {EntityId}: {e.Message}");
            RaiseChanged();
        };
        if (client.Entities.TryGetValue(entityId, out var ent)) Update(ent);
    }

    public string EntityId { get; }
    public string Domain => EntityId.Split('.', 2)[0];
    public override string Id => "ha:" + EntityId;
    public override string Name => _entity?.FriendlyName ?? EntityId;
    public override LightSource Source => LightSource.HomeAssistant;
    public override LightFeatures Features => _entity?.Features ?? LightFeatures.None;
    public override bool IsAvailable =>
        _client.State == HaConnectionState.Connected && _entity is not null && _entity.IsAvailable;

    public override string StatusText
    {
        get
        {
            if (_client.State != HaConnectionState.Connected) return "Home Assistant offline";
            if (_entity is null) return "Entity not found";
            if (!_entity.IsAvailable) return "Unavailable";
            if (_error.Length > 0) return "Error: " + _error;
            return Has(LightFeatures.Action) ? EntityId : _entity.State;
        }
    }

    private IReadOnlyList<string> _effects = Array.Empty<string>();
    public override IReadOnlyList<string> Effects => _effects;

    /// <summary>Novo estado vindo do HA (ou null se a entidade desapareceu).</summary>
    public void Update(HaEntity? e)
    {
        _entity = e;
        if (e is not null)
        {
            IsOn = e.IsOn;
            if (e.Int("brightness") is { } b) Brightness = Math.Clamp(b, 0, 255);
            if (e.Rgb is { } c) Color = c;
            if (e.Int("min_color_temp_kelvin") is { } mn) MinKelvin = mn;
            if (e.Int("max_color_temp_kelvin") is { } mx) MaxKelvin = mx;
            if (e.Int("color_temp_kelvin") is { } k) ColorTempKelvin = k;
            ColorMode = e.Str("color_mode") switch
            {
                "color_temp" => ActiveColorMode.ColorTemp,
                "hs" or "xy" or "rgb" or "rgbw" or "rgbww" => ActiveColorMode.Rgb,
                _ => ActiveColorMode.None,
            };
            var list = e.StrList("effect_list");
            if (!list.SequenceEqual(_effects)) _effects = list;   // mesma instância → a UI não perde a seleção
            var eff = e.Str("effect");
            Effect = eff is null ? -1 : IndexOf(_effects, eff);
        }
        RaiseChanged();
    }

    /// <summary>A ligação ao HA mudou de estado.</summary>
    public void Refresh()
    {
        if (_client.Entities.TryGetValue(EntityId, out var e)) Update(e);
        else Update(null);
    }

    private static int IndexOf(IReadOnlyList<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == v) return i;
        return -1;
    }

    private void Call(string key, string domain, string service, JsonObject? data = null)
    {
        _error = "";
        _queue.Post(key, () => _client.CallServiceAsync(domain, service, EntityId, data));
    }

    public override void SetPower(bool on)
    {
        IsOn = on;
        // fan/switch/input_boolean/light têm todos turn_on / turn_off
        Call("power", Domain, on ? "turn_on" : "turn_off");
        RaiseChanged();
    }

    public override void SetBrightness(int value)
    {
        Brightness = Math.Clamp(value, 0, 255);
        IsOn = Brightness > 0;
        Call("power", "light", Brightness > 0 ? "turn_on" : "turn_off",
            Brightness > 0 ? new JsonObject { ["brightness"] = Brightness } : null);
        RaiseChanged();
    }

    public override void SetColor(RgbColor color)
    {
        Color = color;
        IsOn = true;
        Call("color", "light", "turn_on", new JsonObject { ["rgb_color"] = new JsonArray(color.R, color.G, color.B) });
        RaiseChanged();
    }

    public override void SetColorTemp(int kelvin)
    {
        ColorTempKelvin = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
        IsOn = true;
        Call("color", "light", "turn_on", new JsonObject { ["color_temp_kelvin"] = ColorTempKelvin });
        RaiseChanged();
    }

    public override void SetEffect(int index)
    {
        if (index < 0 || index >= _effects.Count) return;
        Effect = index;
        Call("effect", "light", "turn_on", new JsonObject { ["effect"] = _effects[index] });
        RaiseChanged();
    }

    public override void Activate()
    {
        switch (Domain)
        {
            case "scene": Call("activate", "scene", "turn_on"); break;
            case "script": Call("activate", "script", "turn_on"); break;
            case "button": Call("activate", "button", "press"); break;
        }
    }

    public Task WhenSent() => _queue.WhenIdle();
}
