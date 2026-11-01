namespace Pine3LightControl.Core.Lights;

public enum LightSource { HomeAssistant, OpenRgb, Pc }

/// <summary>Modo de cor em uso (para as cenas guardarem o que interessa).</summary>
public enum ActiveColorMode { None, Rgb, ColorTemp }

[Flags]
public enum LightFeatures
{
    None = 0,
    OnOff = 1,
    Brightness = 2,
    Color = 4,
    ColorTemp = 8,
    Effects = 16,
    Speed = 32,
    White = 64,
    /// <summary>Cena / script / botão: só "ativar", sem estado.</summary>
    Action = 128,
}

/// <summary>Uma luz controlável, venha de onde vier (Bluetooth ou Home Assistant).</summary>
public interface ILight
{
    /// <summary>"ble:AA:BB:..." ou "ha:light.sala".</summary>
    string Id { get; }
    string Name { get; }
    LightSource Source { get; }
    LightFeatures Features { get; }
    bool IsAvailable { get; }
    string StatusText { get; }

    bool IsOn { get; }
    /// <summary>0–255.</summary>
    int Brightness { get; }
    RgbColor Color { get; }
    int ColorTempKelvin { get; }
    int MinKelvin { get; }
    int MaxKelvin { get; }
    /// <summary>Índice em <see cref="Effects"/>, -1 se nenhum.</summary>
    int Effect { get; }
    IReadOnlyList<string> Effects { get; }
    /// <summary>0–255.</summary>
    int Speed { get; }
    /// <summary>Canal branco (RGBW), 0–255.</summary>
    int White { get; }
    ActiveColorMode ColorMode { get; }

    /// <summary>Estado mudou (pode vir de qualquer thread).</summary>
    event Action? Changed;

    void SetPower(bool on);
    void SetBrightness(int value);
    void SetColor(RgbColor color);
    void SetColorTemp(int kelvin);
    void SetEffect(int index);
    void SetSpeed(int value);
    void SetWhite(int value);
    void Activate();
}

/// <summary>Base com estado e notificação; as subclasses só implementam o que suportam.</summary>
public abstract class LightBase : ILight
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract LightSource Source { get; }
    public abstract LightFeatures Features { get; }
    public abstract bool IsAvailable { get; }
    public abstract string StatusText { get; }

    public bool IsOn { get; protected set; }
    public int Brightness { get; protected set; } = 255;
    public RgbColor Color { get; protected set; } = RgbColor.White;
    public int ColorTempKelvin { get; protected set; } = 4000;
    public int MinKelvin { get; protected set; } = 2000;
    public int MaxKelvin { get; protected set; } = 6500;
    public int Effect { get; protected set; } = -1;
    public virtual IReadOnlyList<string> Effects => Array.Empty<string>();
    public int Speed { get; protected set; } = 128;
    public int White { get; protected set; }
    public ActiveColorMode ColorMode { get; protected set; } = ActiveColorMode.None;

    public event Action? Changed;
    protected void RaiseChanged() { try { Changed?.Invoke(); } catch (Exception e) { Log.Write($"[Light] {e.Message}"); } }

    public bool Has(LightFeatures f) => (Features & f) == f;

    public virtual void SetPower(bool on) { }
    public virtual void SetBrightness(int value) { }
    public virtual void SetColor(RgbColor color) { }
    public virtual void SetColorTemp(int kelvin) { }
    public virtual void SetEffect(int index) { }
    public virtual void SetSpeed(int value) { }
    public virtual void SetWhite(int value) { }
    public virtual void Activate() { }

    protected static byte B(int v) => (byte)Math.Clamp(v, 0, 255);
}
