using System.Globalization;

namespace Pine3LightControl.Core.Lights;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor White = new(255, 255, 255);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string? s, out RgbColor c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        c = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbColor Parse(string s) =>
        TryParse(s, out var c) ? c : throw new FormatException($"Cor inválida: {s}");

    /// <summary>Aproximação de temperatura de cor (Kelvin) para RGB — só para a pré-visualização.</summary>
    public static RgbColor FromKelvin(int kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r, g, b;
        if (t <= 66) { r = 255; g = 99.4708025861 * Math.Log(t) - 161.1195681661; }
        else { r = 329.698727446 * Math.Pow(t - 60, -0.1332047592); g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492); }
        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        static byte C(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
        return new RgbColor(C(r), C(g), C(b));
    }
}
