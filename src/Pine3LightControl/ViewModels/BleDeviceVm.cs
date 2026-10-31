using System.Collections.Generic;
using System.Linq;
using Pine3LightControl.Core;
using Pine3LightControl.Core.Bluetooth;

namespace Pine3LightControl.ViewModels;

/// <summary>Controlador Bluetooth na página "Bluetooth".</summary>
public sealed class BleDeviceVm : Observable
{
    private readonly AppConfig _config;

    public BleDeviceVm(Sp110eLight light, AppConfig config)
    {
        Light = light;
        _config = config;
        Sync(force: true);
    }

    public Sp110eLight Light { get; }
    public string Address => Light.Config.Address;
    public string Status => Light.StatusText;
    public bool IsConnected => Light.IsAvailable;
    public bool IsRunning => Light.IsRunning;

    public string Name
    {
        get => Light.Config.Name;
        set
        {
            if (Light.Config.Name == value) return;
            Light.Config.Name = value.Trim();
            _config.Save();
            Raise();
        }
    }

    public bool AutoConnect
    {
        get => Light.Config.AutoConnect;
        set
        {
            if (Light.Config.AutoConnect == value) return;
            Light.Config.AutoConnect = value;
            _config.Save();
            Raise();
        }
    }

    public IReadOnlyList<string> IcModels => Sp110e.IcModels;
    public IReadOnlyList<string> RgbOrders => Sp110e.RgbOrders;

    // Valores a gravar no controlador (começam com o que ele reportou)
    private int _ic = -1, _order = -1;
    private string _pixels = "";
    public int IcModel { get => _ic; set => Set(ref _ic, value); }
    public int RgbOrder { get => _order; set => Set(ref _order, value); }
    public string PixelCount { get => _pixels; set => Set(ref _pixels, value); }

    public string DeviceInfo => Light.HasDeviceInfo
        ? $"Controller reports: {Pick(Sp110e.IcModels, Light.IcModel)}, {Pick(Sp110e.RgbOrders, Light.RgbOrder)}, {Light.PixelCount} LEDs"
        : "Controller settings not read yet (connect first)";

    private static string Pick(string[] list, int i) => i >= 0 && i < list.Length ? list[i] : $"#{i}";

    private bool _filled;
    public void Sync(bool force = false)
    {
        // Preenche os campos de edição na primeira leitura do controlador
        if (Light.HasDeviceInfo && (!_filled || force))
        {
            _filled = true;
            _ic = Light.IcModel;
            _order = Light.RgbOrder;
            _pixels = Light.PixelCount.ToString();
        }
        RaiseAll();
    }
}

/// <summary>Dispositivo encontrado no scan.</summary>
public sealed class ScanResultVm : Observable
{
    public ScanResultVm(string address) => Address = address;

    public string Address { get; }
    private string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(DisplayName)); } }
    private short _rssi;
    public short Rssi { get => _rssi; set { if (Set(ref _rssi, value)) Raise(nameof(Signal)); } }
    private bool _hasFfe0;
    public bool HasFfe0 { get => _hasFfe0; set { if (Set(ref _hasFfe0, value)) Raise(nameof(Hint)); } }
    private bool _added;
    public bool Added { get => _added; set { if (Set(ref _added, value)) Raise(nameof(CanAdd)); } }

    public bool CanAdd => !_added;
    public string DisplayName => string.IsNullOrWhiteSpace(_name) ? "(no name)" : _name;
    public string Signal => $"{_rssi} dBm";

    /// <summary>Os controladores da LED Hue anunciam-se como "SP110E", "SP105E", ...</summary>
    public bool LooksLikeLedHue =>
        _hasFfe0 || (_name.StartsWith("SP1", System.StringComparison.OrdinalIgnoreCase) && _name.Length <= 8);

    public string Hint => LooksLikeLedHue ? "LED Hue controller" : "";
}

/// <summary>Entidade do Home Assistant na lista de escolha.</summary>
public sealed class EntityVm : Observable
{
    private readonly System.Action<string, bool> _onSelect;

    public EntityVm(string entityId, System.Action<string, bool> onSelect)
    {
        EntityId = entityId;
        _onSelect = onSelect;
    }

    public string EntityId { get; }
    public string Domain => EntityId.Split('.', 2)[0];

    private string _name = "", _state = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string State { get => _state; set => Set(ref _state, value); }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _onSelect(EntityId, value);
        }
    }

    /// <summary>Atualiza sem disparar a gravação.</summary>
    public void SetSelectedSilently(bool v)
    {
        _selected = v;
        Raise(nameof(Selected));
    }
}
