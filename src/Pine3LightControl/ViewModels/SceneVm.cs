using System.Collections.Generic;
using System.Linq;
using Pine3LightControl.Core.Scenes;

namespace Pine3LightControl.ViewModels;

/// <summary>Cena na página Scenes e nos botões rápidos da página Lights.</summary>
public sealed class SceneVm : Observable
{
    public SceneVm(Scene scene) => Scene = scene;

    public Scene Scene { get; }
    public string Name => Scene.Name;
    public string Summary => SceneEngine.Summary(Scene);

    /// <summary>Luzes da cena, uma por linha (para a tooltip / detalhe).</summary>
    public string Details => string.Join("\n", Scene.Lights.Select(Describe));

    private static string Describe(SceneLightState s)
    {
        if (s.Activate) return $"{s.Name}: activate";
        if (!s.On) return $"{s.Name}: off";
        var parts = new List<string> { "on" };
        if (s.Brightness is { } b) parts.Add($"{System.Math.Round(b * 100.0 / 255)}%");
        if (s.ColorTempKelvin is { } k) parts.Add($"{k} K");
        if (s.Color is { } c) parts.Add(c);
        if (s.Effect is { } e) parts.Add(e);
        return $"{s.Name}: {string.Join(" · ", parts)}";
    }

    public void Refresh() => RaiseAll();
}

/// <summary>Luz na lista "incluir na nova cena".</summary>
public sealed class SceneLightChoice : Observable
{
    public SceneLightChoice(string id, string name, string now)
    {
        Id = id;
        Name = name;
        Now = now;
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>Estado atual (o que vai ser gravado).</summary>
    public string Now { get; }

    private bool _include;
    public bool Include { get => _include; set => Set(ref _include, value); }
}
