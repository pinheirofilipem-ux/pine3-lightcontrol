using Pine3LightControl.Core;
using Pine3LightControl.Core.Lights;
using Pine3LightControl.Core.Scenes;
using Xunit;

/// <summary>Luz falsa: guarda o estado e regista as chamadas pela ordem.</summary>
sealed class FakeLight : LightBase
{
    private readonly LightFeatures _features;
    private readonly string[] _effects;
    public readonly List<string> Calls = new();

    public FakeLight(string id, LightFeatures f, params string[] effects)
    {
        Id = id;
        _features = f;
        _effects = effects;
    }

    public override string Id { get; }
    public override string Name => Id;
    public LightSource Src = LightSource.HomeAssistant;
    public override LightSource Source => Src;
    public override LightFeatures Features => _features;
    public bool Available = true;
    public override bool IsAvailable => Available;
    public override string StatusText => "";
    public override IReadOnlyList<string> Effects => _effects;

    public void Set(bool on, int bri = 255, RgbColor? c = null, int k = 0, int effect = -1, ActiveColorMode mode = ActiveColorMode.None)
    {
        IsOn = on; Brightness = bri; if (c is { } cc) Color = cc; if (k > 0) ColorTempKelvin = k; Effect = effect; ColorMode = mode;
    }

    public override void SetPower(bool on) { IsOn = on; Calls.Add($"power:{on}"); }
    public override void SetBrightness(int v) { Brightness = v; IsOn = true; Calls.Add($"bri:{v}"); }
    public override void SetColor(RgbColor c) { Color = c; Calls.Add($"color:{c.Hex}"); }
    public override void SetColorTemp(int k) { ColorTempKelvin = k; Calls.Add($"temp:{k}"); }
    public override void SetEffect(int i) { Effect = i; Calls.Add($"effect:{_effects[i]}"); }
    public override void Activate() => Calls.Add("activate");
    public void RaiseChangedForTest() => RaiseChanged();
}

public class SceneTests
{
    private const LightFeatures Rgb = LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color | LightFeatures.Effects;
    private const LightFeatures Bulb = LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color | LightFeatures.ColorTemp;

    [Fact]
    public void Capture_records_what_each_light_is_doing()
    {
        var strip = new FakeLight("ha:light.strip", Rgb, "Static", "Auto", "Effect 1", "Effect 7");
        strip.Set(true, 128, new RgbColor(255, 0, 0), effect: 0);
        var bulb = new FakeLight("ha:light.bulb", Bulb);
        bulb.Set(true, 200, k: 2700, mode: ActiveColorMode.ColorTemp);
        var lamp = new FakeLight("ha:light.lamp", Bulb);
        lamp.Set(false);
        var plug = new FakeLight("ha:switch.plug", LightFeatures.OnOff);
        plug.Set(true);
        var movie = new FakeLight("ha:scene.movie", LightFeatures.Action);

        var s = SceneEngine.Capture("Evening", new ILight[] { strip, bulb, lamp, plug, movie });

        Assert.Equal("Evening", s.Name);
        var st = s.Lights[0];
        Assert.True(st.On);
        Assert.Equal(128, st.Brightness);
        Assert.Equal("#FF0000", st.Color);   // "Static" não conta como efeito
        Assert.Null(st.Effect);
        Assert.Equal(2700, s.Lights[1].ColorTempKelvin);
        Assert.Null(s.Lights[1].Color);
        Assert.False(s.Lights[2].On);
        Assert.Null(s.Lights[2].Brightness);
        Assert.True(s.Lights[3].On);
        Assert.Null(s.Lights[3].Brightness);
        Assert.True(s.Lights[4].Activate);
        Assert.Equal("3 on, 1 off, 1 action", SceneEngine.Summary(s));

        strip.Set(true, 255, effect: 3);
        var e = SceneEngine.CaptureLight(strip);
        Assert.Equal("Effect 7", e.Effect);
        Assert.Null(e.Color);
    }

    [Fact]
    public void Pc_lights_keep_effect_and_color_together()
    {
        // Teclado VIA: "Solid Color" é um efeito com cor; tem de voltar com a cor certa
        var kb = new FakeLight("pc:keyboard-keys", Rgb, "All Off", "Solid Color", "Breathing") { Src = LightSource.Pc };
        kb.Set(true, 200, new RgbColor(0, 0, 255), effect: 1);
        var st = SceneEngine.CaptureLight(kb);
        Assert.Equal("Solid Color", st.Effect);
        Assert.Equal("#0000FF", st.Color);

        kb.Set(true, 200, new RgbColor(255, 0, 0), effect: 2);
        kb.Calls.Clear();
        SceneEngine.Apply(new Scene { Lights = { st } }, _ => kb);
        Assert.Equal(new[] { "power:True", "bri:200", "color:#0000FF", "effect:Solid Color" }, kb.Calls);
        Assert.Equal(1, kb.Effect);
        Assert.Equal(new RgbColor(0, 0, 255), kb.Color);
    }

    [Fact]
    public void Apply_sets_each_light_in_order_and_counts_missing()
    {
        var strip = new FakeLight("a", Rgb, "Static", "Auto", "Effect 7");
        var bulb = new FakeLight("b", Bulb);
        var gone = new FakeLight("c", Bulb) { Available = false };
        var movie = new FakeLight("d", LightFeatures.Action);
        var lights = new[] { strip, bulb, gone, movie }.ToDictionary(l => l.Id, l => (ILight)l);

        var scene = new Scene
        {
            Name = "x",
            Lights =
            {
                new SceneLightState { LightId = "a", On = true, Brightness = 50, Effect = "effect 7" },
                new SceneLightState { LightId = "b", On = true, Brightness = 10, ColorTempKelvin = 3000, Color = "#00FF00" },
                new SceneLightState { LightId = "c", On = false },
                new SceneLightState { LightId = "d", Activate = true },
                new SceneLightState { LightId = "zzz", On = true },
            },
        };

        int missing = SceneEngine.Apply(scene, id => lights.GetValueOrDefault(id));

        Assert.Equal(2, missing);   // "c" indisponível, "zzz" não existe
        Assert.Equal(new[] { "power:True", "bri:50", "effect:Effect 7" }, strip.Calls);
        Assert.Equal(new[] { "power:True", "bri:10", "temp:3000" }, bulb.Calls);   // temperatura tem prioridade
        Assert.Empty(gone.Calls);
        Assert.Equal(new[] { "activate" }, movie.Calls);
    }

    [Fact]
    public void Peripherals_stay_out_of_scenes_and_keep_their_last_state()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "p3lc-test-" + Guid.NewGuid());
        var cfg = new AppConfig();
        // Cena antiga (≤ 0.5) com o teclado: sai ao abrir
        cfg.Scenes.Add(new Scene
        {
            Name = "Old",
            Lights =
            {
                new SceneLightState { LightId = "pc:keyboard-keys", On = true },
                new SceneLightState { LightId = "pc:gpu", On = true },
            },
        });
        var hub = new LightHub(cfg);
        Assert.Equal(new[] { "pc:gpu" }, hub.Scenes[0].Lights.Select(l => l.LightId));

        var gpu = new FakeLight("pc:gpu", Rgb, "Static") { Src = LightSource.Pc };
        var kb = new FakeLight("pc:keyboard-keys", Rgb, "All Off", "Solid Color") { Src = LightSource.Pc };
        var mouse = new FakeLight("pc:mouse", Rgb, "Static") { Src = LightSource.Pc };
        hub.SetPcLights(new ILight[] { gpu, kb, mouse });

        Assert.Equal(new[] { "pc:keyboard-keys", "pc:mouse" }, hub.Peripherals.Select(l => l.Id));
        Assert.Equal(new[] { "pc:gpu" }, hub.SceneLights.Select(l => l.Id));
        var sc = hub.CreateScene("New", new[] { "pc:gpu", "pc:keyboard-keys", "pc:mouse" });
        Assert.Equal(new[] { "pc:gpu" }, sc.Lights.Select(l => l.LightId));
        hub.SetAllPower(false);
        Assert.Empty(kb.Calls);                                   // "desligar tudo" não mexe no teclado

        // O teclado muda: o estado fica guardado
        kb.Set(true, 180, new RgbColor(0, 255, 0), effect: 1);
        kb.RaiseChangedForTest();
        hub.FlushPcState();
        var saved = AppConfig.Load().PcRgb.LastState["pc:keyboard-keys"];
        Assert.Equal("#00FF00", saved.Color);
        Assert.Equal("Solid Color", saved.Effect);

        // Teclado desligado e ligado outra vez ao cabo: o novo recebe o estado guardado,
        // a gráfica (que já existia) não é tocada
        gpu.Calls.Clear();
        var kb2 = new FakeLight("pc:keyboard-keys", Rgb, "All Off", "Solid Color") { Src = LightSource.Pc };
        hub.SetPcLights(new ILight[] { gpu, mouse });
        hub.SetPcLights(new ILight[] { gpu, mouse, kb2 });
        Assert.Equal(new[] { "power:True", "bri:180", "color:#00FF00", "effect:Solid Color" }, kb2.Calls);
        Assert.Empty(gpu.Calls);
    }

    [Fact]
    public void Hub_creates_updates_renames_moves_and_deletes_scenes()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "p3lc-test-" + Guid.NewGuid());
        var cfg = new AppConfig();
        var hub = new LightHub(cfg);
        int changes = 0;
        hub.ScenesChanged += () => changes++;

        var a = hub.CreateScene("  Work ", new[] { "ha:light.missing" });
        var b = hub.CreateScene("Movie", Array.Empty<string>());
        Assert.Equal("Work", a.Name);
        Assert.Empty(a.Lights);   // luz que não existe é ignorada na captura

        hub.RenameScene(a, "Focus");
        hub.MoveScene(b, -1);
        Assert.Equal(new[] { "Movie", "Focus" }, hub.Scenes.Select(s => s.Name));

        a.Lights.Add(new SceneLightState { LightId = "ha:light.offline", On = true });
        hub.UpdateScene(a);       // luz offline mantém o estado antigo
        Assert.Single(a.Lights);

        hub.DeleteScene(b);
        Assert.Equal(new[] { "Focus" }, AppConfig.Load().Scenes.Select(s => s.Name));
        Assert.True(changes >= 5);
    }
}
