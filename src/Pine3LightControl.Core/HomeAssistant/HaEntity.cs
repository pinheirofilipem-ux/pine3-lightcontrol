using System.Text.Json;
using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.HomeAssistant;

/// <summary>Estado de uma entidade do Home Assistant (cópia imutável do JSON).</summary>
public sealed class HaEntity
{
    /// <summary>Domínios que a app sabe controlar.</summary>
    public static readonly string[] SupportedDomains =
        { "light", "switch", "input_boolean", "fan", "scene", "script", "button" };

    private static readonly string[] ColorModes = { "hs", "xy", "rgb", "rgbw", "rgbww" };

    public string EntityId { get; }
    public string State { get; }
    public JsonElement Attributes { get; }

    public HaEntity(string entityId, string state, JsonElement attributes)
    {
        EntityId = entityId;
        State = state;
        Attributes = attributes.ValueKind == JsonValueKind.Object ? attributes.Clone() : default;
    }

    /// <summary>Lê um objeto de estado tal como vem do get_states / state_changed.</summary>
    public static HaEntity? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("entity_id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        var state = e.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "unknown";
        e.TryGetProperty("attributes", out var attrs);
        return new HaEntity(id.GetString()!, state, attrs);
    }

    public string Domain => EntityId.Split('.', 2)[0];
    public bool IsSupported => SupportedDomains.Contains(Domain);
    public string FriendlyName => Str("friendly_name") ?? EntityId;
    public bool IsOn => State == "on";
    public bool IsAvailable => State != "unavailable";

    public string? Str(string name) =>
        Attributes.ValueKind == JsonValueKind.Object && Attributes.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int? Int(string name) =>
        Attributes.ValueKind == JsonValueKind.Object && Attributes.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (int)Math.Round(d) : null;

    public IReadOnlyList<string> StrList(string name)
    {
        if (Attributes.ValueKind != JsonValueKind.Object || !Attributes.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
    }

    public RgbColor? Rgb
    {
        get
        {
            if (Attributes.ValueKind != JsonValueKind.Object || !Attributes.TryGetProperty("rgb_color", out var v) ||
                v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 3) return null;
            var a = v.EnumerateArray().Select(x => x.TryGetDouble(out var d) ? (byte)Math.Clamp((int)d, 0, 255) : (byte)0).ToArray();
            return new RgbColor(a[0], a[1], a[2]);
        }
    }

    /// <summary>O que esta entidade permite fazer.</summary>
    public LightFeatures Features
    {
        get
        {
            switch (Domain)
            {
                case "scene":
                case "script":
                case "button":
                    return LightFeatures.Action;
                case "light":
                    var f = LightFeatures.OnOff;
                    var modes = StrList("supported_color_modes");
                    if (modes.Any(m => m != "onoff")) f |= LightFeatures.Brightness;
                    if (modes.Any(m => ColorModes.Contains(m))) f |= LightFeatures.Color;
                    if (modes.Contains("color_temp")) f |= LightFeatures.ColorTemp;
                    if (StrList("effect_list").Count > 0) f |= LightFeatures.Effects;
                    return f;
                default:
                    return LightFeatures.OnOff;
            }
        }
    }
}
