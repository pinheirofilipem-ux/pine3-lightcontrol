using Pine3LightControl.Core.HomeAssistant;
using Pine3LightControl.Core.OpenRgb;
using Pine3LightControl.Core.Scenes;

namespace Pine3LightControl.Core.Lights;

/// <summary>
/// Junta todas as luzes (entidades escolhidas do Home Assistant) e as cenas, e
/// mantém-nas em sincronia com a configuração.
/// </summary>
public sealed class LightHub : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<HaLight> _ha = new();
    private readonly List<OpenRgbLight> _orgb = new();
    private readonly List<ILight> _pc = new();

    public LightHub(AppConfig config)
    {
        Config = config;
        Ha.EntityUpdated += e =>
        {
            HaLight? l;
            lock (_gate) l = _ha.FirstOrDefault(x => x.EntityId == e.EntityId);
            l?.Update(e);
        };
        Ha.EntitiesReloaded += RefreshHa;
        Ha.ConnectionChanged += RefreshHa;
        OpenRgb.DevicesReloaded += () => SyncOpenRgbLights();
        OpenRgb.ConnectionChanged += () =>
        {
            foreach (var l in OpenRgbLights) l.ConnectionChanged();
        };
    }

    public AppConfig Config { get; }
    public HaClient Ha { get; } = new();
    public OpenRgbClient OpenRgb { get; } = new();

    /// <summary>Luzes adicionadas/removidas.</summary>
    public event Action? ListChanged;
    /// <summary>Estado de uma luz mudou (qualquer thread).</summary>
    public event Action<ILight>? LightChanged;
    /// <summary>Cenas criadas, alteradas, removidas ou reordenadas.</summary>
    public event Action? ScenesChanged;

    public IReadOnlyList<ILight> Lights
    {
        get { lock (_gate) return _pc.Concat(_orgb).Concat(_ha).ToArray(); }
    }

    /// <summary>Luzes do próprio PC controladas diretamente (gráfica, memórias).</summary>
    public IReadOnlyList<ILight> PcLights
    {
        get { lock (_gate) return _pc.ToArray(); }
    }

    /// <summary>A app deteta o hardware e entrega aqui as luzes encontradas.</summary>
    public void SetPcLights(IEnumerable<ILight> lights)
    {
        var list = lights.ToList();
        foreach (var l in list)
        {
            var captured = l;
            l.Changed += () => LightChanged?.Invoke(captured);
        }
        List<ILight> old;
        lock (_gate)
        {
            old = _pc.ToList();
            _pc.Clear();
            _pc.AddRange(list);
        }
        foreach (var o in old.Except(list)) (o as IDisposable)?.Dispose();
        ListChanged?.Invoke();
    }

    public IReadOnlyList<OpenRgbLight> OpenRgbLights
    {
        get { lock (_gate) return _orgb.ToArray(); }
    }

    public IReadOnlyList<HaLight> HaLights
    {
        get { lock (_gate) return _ha.ToArray(); }
    }

    public ILight? FindLight(string id) => Lights.FirstOrDefault(l => l.Id == id);

    public async Task StartAsync()
    {
        SyncHaLights();
        SyncOpenRgbLights();
        await ReconnectHaAsync().ConfigureAwait(false);
        await ReconnectOpenRgbAsync().ConfigureAwait(false);
    }

    // ── OpenRGB ──────────────────────────────────────────────────────────────

    public Task ReconnectOpenRgbAsync()
    {
        var o = Config.OpenRgb;
        return o.Enabled ? OpenRgb.RestartAsync(o.Host, o.Port) : OpenRgb.StopAsync();
    }

    /// <summary>Grupos de dispositivos que o OpenRGB está a mostrar (nome, tipo, quantidade).</summary>
    public IReadOnlyList<(string Name, string Type, int Count)> OpenRgbGroups =>
        OpenRgb.Devices.GroupBy(d => d.Name)
            .Select(g => (g.Key, g.First().TypeName, g.Count()))
            .ToArray();

    public bool IsOpenRgbSelected(string group) => Config.OpenRgb.Devices.Contains(group);

    public void SetOpenRgbSelected(string group, bool selected)
    {
        var list = Config.OpenRgb.Devices;
        if (selected == list.Contains(group)) return;
        if (selected) list.Add(group); else list.Remove(group);
        Config.Save();
        SyncOpenRgbLights();
    }

    private void SyncOpenRgbLights()
    {
        var devices = OpenRgb.Devices;
        var created = new List<OpenRgbLight>();
        bool changed = false;
        lock (_gate)
        {
            var keep = new List<OpenRgbLight>();
            foreach (var name in Config.OpenRgb.Devices)
            {
                var l = _orgb.FirstOrDefault(x => x.GroupName == name);
                if (l is null)
                {
                    l = new OpenRgbLight(OpenRgb, name);
                    var captured = l;
                    l.Changed += () => LightChanged?.Invoke(captured);
                    created.Add(l);
                    changed = true;
                }
                keep.Add(l);
            }
            if (keep.Count != _orgb.Count || !keep.SequenceEqual(_orgb)) changed = true;
            _orgb.Clear();
            _orgb.AddRange(keep);
        }
        // Luz nova lê a cor que os LEDs têm agora; as existentes mantêm o estado da app
        foreach (var l in OpenRgbLights)
            l.Update(devices.Where(d => d.Name == l.GroupName), readState: created.Contains(l) || !l.HasDevices);
        if (changed) ListChanged?.Invoke();
    }

    // ── Home Assistant ───────────────────────────────────────────────────────

    public Task ReconnectHaAsync()
    {
        var h = Config.HomeAssistant;
        return Ha.RestartAsync(h.Url, h.IsConfigured ? h.Token : "", h.AllowInvalidCertificate);
    }

    public bool IsHaEntitySelected(string entityId) => Config.HomeAssistant.Entities.Contains(entityId);

    public void SetHaEntitySelected(string entityId, bool selected)
    {
        var list = Config.HomeAssistant.Entities;
        if (selected == list.Contains(entityId)) return;
        if (selected) list.Add(entityId); else list.Remove(entityId);
        Config.Save();
        SyncHaLights();
    }

    /// <summary>Muda a posição de uma entidade no painel.</summary>
    public void MoveHaEntity(string entityId, int delta)
    {
        var list = Config.HomeAssistant.Entities;
        int i = list.IndexOf(entityId), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Config.Save();
        SyncHaLights();
    }

    private void SyncHaLights()
    {
        bool changed = false;
        lock (_gate)
        {
            var wanted = Config.HomeAssistant.Entities;
            var keep = new List<HaLight>();
            foreach (var id in wanted)
            {
                var l = _ha.FirstOrDefault(x => x.EntityId == id);
                if (l is null)
                {
                    l = new HaLight(Ha, id);
                    var captured = l;
                    l.Changed += () => LightChanged?.Invoke(captured);
                    changed = true;
                }
                keep.Add(l);
            }
            if (!changed && keep.Count == _ha.Count && keep.SequenceEqual(_ha)) return;
            _ha.Clear();
            _ha.AddRange(keep);
        }
        ListChanged?.Invoke();
    }

    private void RefreshHa()
    {
        foreach (var l in HaLights) l.Refresh();
    }

    // ── Cenas ────────────────────────────────────────────────────────────────

    public IReadOnlyList<Scene> Scenes => Config.Scenes.ToArray();

    /// <summary>Cria uma cena com o estado atual das luzes indicadas.</summary>
    public Scene CreateScene(string name, IEnumerable<string> lightIds)
    {
        var scene = SceneEngine.Capture(name, lightIds.Select(FindLight).OfType<ILight>());
        Config.Scenes.Add(scene);
        Config.Save();
        Log.Write($"[Cenas] criada \"{scene.Name}\" ({scene.Lights.Count} luzes)");
        ScenesChanged?.Invoke();
        return scene;
    }

    /// <summary>Volta a gravar a cena com o estado atual das mesmas luzes.</summary>
    public void UpdateScene(Scene scene)
    {
        var fresh = SceneEngine.Capture(scene.Name, scene.Lights.Select(s => FindLight(s.LightId)).OfType<ILight>());
        // Luzes que agora não existem (entidade desmarcada / HA offline) mantêm o estado antigo
        foreach (var old in scene.Lights)
            if (fresh.Lights.All(f => f.LightId != old.LightId)) fresh.Lights.Add(old);
        scene.Lights = fresh.Lights;
        Config.Save();
        ScenesChanged?.Invoke();
    }

    public void RenameScene(Scene scene, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == scene.Name) return;
        scene.Name = name;
        Config.Save();
        ScenesChanged?.Invoke();
    }

    public void DeleteScene(Scene scene)
    {
        if (!Config.Scenes.Remove(scene)) return;
        Config.Save();
        ScenesChanged?.Invoke();
    }

    public void MoveScene(Scene scene, int delta)
    {
        var list = Config.Scenes;
        int i = list.IndexOf(scene), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Config.Save();
        ScenesChanged?.Invoke();
    }

    /// <summary>Aplica a cena. Devolve quantas luzes não estavam disponíveis.</summary>
    public int ActivateScene(Scene scene)
    {
        var missing = SceneEngine.Apply(scene, FindLight);
        Log.Write($"[Cenas] \"{scene.Name}\" ativada{(missing > 0 ? $" ({missing} luz(es) indisponível(eis))" : "")}");
        return missing;
    }

    // ── Ações globais ────────────────────────────────────────────────────────

    public void SetAllPower(bool on)
    {
        foreach (var l in Lights)
            if (l.IsAvailable && (l.Features & LightFeatures.OnOff) != 0) l.SetPower(on);
    }

    public async ValueTask DisposeAsync()
    {
        await Ha.DisposeAsync().ConfigureAwait(false);
        await OpenRgb.DisposeAsync().ConfigureAwait(false);
        foreach (var l in PcLights) (l as IDisposable)?.Dispose();
    }
}
