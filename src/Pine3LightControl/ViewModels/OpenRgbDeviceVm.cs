namespace Pine3LightControl.ViewModels;

/// <summary>Grupo de dispositivos do OpenRGB na página PC RGB.</summary>
public sealed class OpenRgbDeviceVm : Observable
{
    private readonly System.Action<string, bool> _onSelect;

    public OpenRgbDeviceVm(string name, string type, int count, bool selected, System.Action<string, bool> onSelect)
    {
        Name = name;
        Type = type;
        Count = count;
        _selected = selected;
        _onSelect = onSelect;
    }

    public string Name { get; }
    public string Type { get; }
    public int Count { get; }
    public string Detail => Count > 1 ? $"{Type} · {Count} devices (controlled together)" : Type;

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _onSelect(Name, value);
        }
    }
}
