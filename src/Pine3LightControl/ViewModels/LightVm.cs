using System;
using System.Collections.Generic;
using System.Windows.Media;
using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.ViewModels;

/// <summary>
/// Cartão de uma luz. Os setters enviam logo para a luz (que agrega os envios);
/// enquanto o utilizador mexe num controlo, as atualizações vindas do dispositivo
/// não lhe "puxam" o valor (janela de 1,5 s por propriedade).
/// </summary>
public sealed class LightVm : Observable
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1500);
    private readonly Dictionary<string, DateTime> _editing = new();

    public LightVm(ILight light) => Light = light;

    public ILight Light { get; }

    private bool Has(LightFeatures f) => (Light.Features & f) == f;
    private void Touch(string prop) => _editing[prop] = DateTime.UtcNow + Hold;

    // ── Cabeçalho ──
    public string Name => Light.Name;
    public string Status => Light.StatusText;
    public bool IsAvailable => Light.IsAvailable;
    public string SourceText => Light.Source == LightSource.OpenRgb ? "OpenRGB" : "Home Assistant";
    public string SourceGlyph => Light.Source == LightSource.OpenRgb ? "\uE7F8" : "\uE80F";
    public double CardOpacity => IsAvailable ? 1.0 : 0.6;

    // ── Capacidades ──
    public bool HasPower => Has(LightFeatures.OnOff);
    public bool HasBrightness => Has(LightFeatures.Brightness);
    public bool HasColor => Has(LightFeatures.Color);
    public bool HasColorTemp => Has(LightFeatures.ColorTemp);
    public bool HasEffects => Has(LightFeatures.Effects) && Light.Effects.Count > 0;
    public bool HasSpeed => Has(LightFeatures.Speed);
    public bool HasWhite => Has(LightFeatures.White);
    public bool IsAction => Has(LightFeatures.Action);

    // ── Valores ──
    private bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value)) return;
            Touch(nameof(IsOn));
            Light.SetPower(value);
            Raise(nameof(PreviewBrush));
        }
    }

    private double _brightness;
    /// <summary>1–100 %.</summary>
    public double Brightness
    {
        get => _brightness;
        set
        {
            value = Math.Round(Math.Clamp(value, 1, 100));
            if (!Set(ref _brightness, value)) return;
            Touch(nameof(Brightness));
            Light.SetBrightness((int)Math.Round(value * 255 / 100));
            Raise(nameof(BrightnessText));
        }
    }
    public string BrightnessText => $"{_brightness:0}%";

    // O slider vai de 0 a 100 (fixo) e converte-se aqui para Kelvin: um slider com
    // Minimum/Maximum ligados por binding pode "corrigir" o valor e reenviá-lo à luz.
    private double _colorTemp;
    public double ColorTemp
    {
        get => _colorTemp;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _colorTemp, value)) return;
            Touch(nameof(ColorTemp));
            Light.SetColorTemp(PctToKelvin(value));
            Raise(nameof(ColorTempText));
        }
    }
    public string ColorTempText => $"{PctToKelvin(_colorTemp)} K";

    private int PctToKelvin(double pct) =>
        (int)(Math.Round((Light.MinKelvin + pct * (Light.MaxKelvin - Light.MinKelvin) / 100) / 50) * 50);

    private double KelvinToPct(int k) => Light.MaxKelvin <= Light.MinKelvin ? 0 :
        Math.Clamp(Math.Round((k - Light.MinKelvin) * 100.0 / (Light.MaxKelvin - Light.MinKelvin)), 0, 100);

    public IReadOnlyList<string> Effects => Light.Effects;

    private int _effect = -1;
    public int EffectIndex
    {
        get => _effect;
        set
        {
            if (value < 0 || !Set(ref _effect, value)) return;
            Touch(nameof(EffectIndex));
            Light.SetEffect(value);
        }
    }

    private double _speed;
    public double Speed
    {
        get => _speed;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _speed, value)) return;
            Touch(nameof(Speed));
            Light.SetSpeed((int)Math.Round(value * 255 / 100));
        }
    }

    private double _white;
    public double White
    {
        get => _white;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _white, value)) return;
            Touch(nameof(White));
            Light.SetWhite((int)Math.Round(value * 255 / 100));
        }
    }

    public SolidColorBrush ColorBrush => Brush(Light.Color);

    /// <summary>Bolinha do cabeçalho: cor atual se ligada, cinzento se desligada.</summary>
    public Brush PreviewBrush => IsAction ? Brushes.Transparent :
        !_isOn ? new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) :
        HasColor ? ColorBrush :
        HasColorTemp ? Brush(RgbColor.FromKelvin(Light.ColorTempKelvin)) :
        Brush(RgbColor.FromKelvin(3000));

    private static SolidColorBrush Brush(RgbColor c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public void SetColor(RgbColor c)
    {
        Light.SetColor(c);
        _isOn = Light.IsOn;
        Sync();
    }

    public void Activate() => Light.Activate();

    /// <summary>Lê o estado da luz (chamar na thread da UI).</summary>
    public void Sync()
    {
        var now = DateTime.UtcNow;
        bool Free(string p) => !_editing.TryGetValue(p, out var until) || until < now;

        if (Free(nameof(IsOn))) _isOn = Light.IsOn;
        if (Free(nameof(Brightness))) _brightness = Math.Clamp(Math.Round(Light.Brightness * 100.0 / 255), 1, 100);
        if (Free(nameof(ColorTemp))) _colorTemp = KelvinToPct(Light.ColorTempKelvin);
        if (Free(nameof(EffectIndex))) _effect = Light.Effect;
        if (Free(nameof(Speed))) _speed = Math.Round(Light.Speed * 100.0 / 255);
        if (Free(nameof(White))) _white = Math.Round(Light.White * 100.0 / 255);
        RaiseAll();
    }
}
