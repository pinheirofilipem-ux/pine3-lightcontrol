using Pine3LightControl.Core.Bluetooth;
using Pine3LightControl.Core.HomeAssistant;

namespace Pine3LightControl.Core.Lights;

/// <summary>
/// Junta todas as luzes (controladores Bluetooth + entidades escolhidas do Home
/// Assistant) e mantém-nas em sincronia com a configuração.
/// </summary>
public sealed class LightHub : IAsyncDisposable
{
    private readonly Func<BleDeviceConfig, IBleLink> _linkFactory;
    private readonly object _gate = new();
    private readonly List<Sp110eLight> _ble = new();
    private readonly List<HaLight> _ha = new();

    public LightHub(AppConfig config, Func<BleDeviceConfig, IBleLink> linkFactory)
    {
        Config = config;
        _linkFactory = linkFactory;
        Ha.EntityUpdated += e =>
        {
            HaLight? l;
            lock (_gate) l = _ha.FirstOrDefault(x => x.EntityId == e.EntityId);
            l?.Update(e);
        };
        Ha.EntitiesReloaded += RefreshHa;
        Ha.ConnectionChanged += RefreshHa;
    }

    public AppConfig Config { get; }
    public HaClient Ha { get; } = new();

    /// <summary>Luzes adicionadas/removidas.</summary>
    public event Action? ListChanged;
    /// <summary>Estado de uma luz mudou (qualquer thread).</summary>
    public event Action<ILight>? LightChanged;

    public IReadOnlyList<ILight> Lights
    {
        get { lock (_gate) return _ble.Cast<ILight>().Concat(_ha).ToArray(); }
    }

    public IReadOnlyList<Sp110eLight> BleLights
    {
        get { lock (_gate) return _ble.ToArray(); }
    }

    public IReadOnlyList<HaLight> HaLights
    {
        get { lock (_gate) return _ha.ToArray(); }
    }

    public async Task StartAsync()
    {
        foreach (var d in Config.BleDevices) AddBle(d, start: d.AutoConnect);
        SyncHaLights();
        await ReconnectHaAsync().ConfigureAwait(false);
    }

    // ── Bluetooth ────────────────────────────────────────────────────────────

    private Sp110eLight AddBle(BleDeviceConfig d, bool start)
    {
        var light = new Sp110eLight(d, _linkFactory(d));
        light.Changed += () => LightChanged?.Invoke(light);
        lock (_gate) _ble.Add(light);
        if (start) light.Start();
        return light;
    }

    public Sp110eLight AddBleDevice(string address, string name)
    {
        var existing = BleLights.FirstOrDefault(l => string.Equals(l.Config.Address, address, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var d = new BleDeviceConfig { Address = address.ToUpperInvariant(), Name = name, AutoConnect = true };
        Config.BleDevices.Add(d);
        Config.Save();
        var light = AddBle(d, start: true);
        Log.Write($"[BLE] adicionado {name} ({address})");
        ListChanged?.Invoke();
        return light;
    }

    public async Task RemoveBleDeviceAsync(Sp110eLight light)
    {
        lock (_gate) _ble.Remove(light);
        Config.BleDevices.Remove(light.Config);
        Config.Save();
        ListChanged?.Invoke();
        await light.DisposeAsync().ConfigureAwait(false);
        Log.Write($"[BLE] removido {light.Name}");
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

    // ── Ações globais ────────────────────────────────────────────────────────

    public void SetAllPower(bool on)
    {
        foreach (var l in Lights)
            if (l.IsAvailable && (l.Features & LightFeatures.OnOff) != 0) l.SetPower(on);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var l in BleLights)
            try { await l.DisposeAsync().ConfigureAwait(false); } catch { }
        await Ha.DisposeAsync().ConfigureAwait(false);
    }
}
