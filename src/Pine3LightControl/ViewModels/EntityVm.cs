namespace Pine3LightControl.ViewModels;

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
