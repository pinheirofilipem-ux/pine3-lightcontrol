using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.Scenes;

/// <summary>Estado de uma luz dentro de uma cena.</summary>
public sealed class SceneLightState
{
    /// <summary>Id da luz (ex.: "ha:light.sala").</summary>
    public string LightId { get; set; } = "";
    /// <summary>Nome na altura em que foi gravada (para mostrar se a luz desaparecer).</summary>
    public string Name { get; set; } = "";
    public bool On { get; set; }
    /// <summary>0–255; null = não mexer.</summary>
    public int? Brightness { get; set; }
    /// <summary>"#RRGGBB"; null = não mexer.</summary>
    public string? Color { get; set; }
    public int? ColorTempKelvin { get; set; }
    /// <summary>Nome do efeito (ex.: "Effect 7"); null = nenhum.</summary>
    public string? Effect { get; set; }
    /// <summary>Cena/script/botão do HA: só ativar.</summary>
    public bool Activate { get; set; }
}

/// <summary>Uma cena: várias luzes, cada uma com o seu estado.</summary>
public sealed class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<SceneLightState> Lights { get; set; } = new();
}

/// <summary>Captura e aplicação de cenas (sem estado; testável com luzes falsas).</summary>
public static class SceneEngine
{
    public static Scene Capture(string name, IEnumerable<ILight> lights)
    {
        var scene = new Scene { Name = string.IsNullOrWhiteSpace(name) ? "Scene" : name.Trim() };
        foreach (var l in lights) scene.Lights.Add(CaptureLight(l));
        return scene;
    }

    public static SceneLightState CaptureLight(ILight l)
    {
        var s = new SceneLightState { LightId = l.Id, Name = l.Name };
        bool Has(LightFeatures f) => (l.Features & f) == f;
        if (Has(LightFeatures.Action))
        {
            s.Activate = true;
            return s;
        }
        s.On = l.IsOn;
        if (!l.IsOn) return s;   // desligada: basta desligar
        if (Has(LightFeatures.Brightness)) s.Brightness = l.Brightness;

        var effect = Has(LightFeatures.Effects) && l.Effect >= 0 && l.Effect < l.Effects.Count ? l.Effects[l.Effect] : null;
        if (effect is not null && !IsStaticEffect(effect)) s.Effect = effect;

        if (Has(LightFeatures.ColorTemp) && (l.ColorMode == ActiveColorMode.ColorTemp || !Has(LightFeatures.Color)))
            s.ColorTempKelvin = l.ColorTempKelvin;
        else if (Has(LightFeatures.Color) && s.Effect is null)
            s.Color = l.Color.Hex;
        return s;
    }

    /// <summary>"Static"/"Solid"/"None" não são efeitos a reproduzir: a cor chega.</summary>
    private static bool IsStaticEffect(string e) =>
        e.Equals("Static", StringComparison.OrdinalIgnoreCase) ||
        e.Equals("Solid", StringComparison.OrdinalIgnoreCase) ||
        e.Equals("None", StringComparison.OrdinalIgnoreCase);

    /// <summary>Aplica a cena; devolve o número de luzes indisponíveis.</summary>
    public static int Apply(Scene scene, Func<string, ILight?> find)
    {
        int missing = 0;
        foreach (var s in scene.Lights)
        {
            var l = find(s.LightId);
            if (l is null || !l.IsAvailable)
            {
                missing++;
                continue;
            }
            bool Has(LightFeatures f) => (l.Features & f) == f;
            if (s.Activate)
            {
                if (Has(LightFeatures.Action)) l.Activate();
                continue;
            }
            if (!s.On)
            {
                if (Has(LightFeatures.OnOff)) l.SetPower(false);
                continue;
            }
            if (Has(LightFeatures.OnOff)) l.SetPower(true);
            if (s.Brightness is { } b && Has(LightFeatures.Brightness)) l.SetBrightness(b);
            if (s.ColorTempKelvin is { } k && Has(LightFeatures.ColorTemp)) l.SetColorTemp(k);
            else if (RgbColor.TryParse(s.Color, out var c) && Has(LightFeatures.Color)) l.SetColor(c);
            if (s.Effect is { } e && Has(LightFeatures.Effects))
            {
                var i = IndexOf(l.Effects, e);
                if (i >= 0) l.SetEffect(i);
            }
        }
        return missing;
    }

    private static int IndexOf(IReadOnlyList<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Resumo para a UI (ex.: "3 on, 1 off").</summary>
    public static string Summary(Scene scene)
    {
        int on = scene.Lights.Count(l => !l.Activate && l.On);
        int off = scene.Lights.Count(l => !l.Activate && !l.On);
        int act = scene.Lights.Count(l => l.Activate);
        var parts = new List<string>();
        if (on > 0) parts.Add($"{on} on");
        if (off > 0) parts.Add($"{off} off");
        if (act > 0) parts.Add($"{act} action{(act > 1 ? "s" : "")}");
        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }
}
