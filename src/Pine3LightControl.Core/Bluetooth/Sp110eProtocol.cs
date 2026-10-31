using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.Bluetooth;

/// <summary>Estado devolvido pelo controlador (resposta a GET_INFO / CHECK_DEVICE).</summary>
public sealed record Sp110eStatus(
    bool IsOn, int Mode, int Speed, int Brightness, int IcModel, int RgbOrder,
    int PixelCount, RgbColor Color, int White);

/// <summary>
/// Protocolo BLE dos controladores Sperll SP110E / SP105E (app "LED Hue").
/// Serviço 0xFFE0, característica 0xFFE1 (escrita + notificações).
/// Cada comando tem 4 bytes: [d0, d1, d2, comando]. Fontes: gist de mbullington
/// "SP110E protocol" e o driver Python roslovets/SP110E.
/// </summary>
public static class Sp110e
{
    public static readonly Guid ServiceUuid = new("0000ffe0-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CharacteristicUuid = new("0000ffe1-0000-1000-8000-00805f9b34fb");

    public const byte CmdOn = 0xAA;
    public const byte CmdOff = 0xAB;
    public const byte CmdColor = 0x1E;
    public const byte CmdBrightness = 0x2A;
    public const byte CmdWhite = 0x69;
    public const byte CmdMode = 0x2C;
    public const byte CmdModeAuto = 0x06;
    public const byte CmdSpeed = 0x03;
    public const byte CmdIcModel = 0x1C;
    public const byte CmdRgbOrder = 0x3C;
    public const byte CmdPixelCount = 0x2D;
    public const byte CmdCheckDevice = 0xD5;
    public const byte CmdGetInfo = 0x10;
    public const byte CmdRename = 0xBB;

    /// <summary>Modos: 0 = automático (percorre os efeitos), 1–120 = efeitos, 121 = cor fixa.</summary>
    public const int ModeAuto = 0;
    public const int ModeStatic = 121;
    public const int MaxPixels = 1024;

    public static readonly string[] IcModels =
    {
        "SM16703", "TM1804", "UCS1903", "WS2811", "WS2801", "SK6812", "LPD6803", "LPD8806",
        "APA102", "APA105", "DMX512", "TM1914", "TM1913", "P9813", "INK1003", "P943S",
        "P9411", "P9413", "TX1812", "TX1813", "GS8206", "GS8208", "SK9822", "TM1814",
        "SK6812_RGBW", "P9414", "PG412",
    };

    public static readonly string[] RgbOrders = { "RGB", "RBG", "GRB", "GBR", "BRG", "BGR" };

    /// <summary>ICs com canal branco (o driver de referência trata os índices &gt; 22 como RGBW).</summary>
    public static bool IsRgbw(int icModel) => icModel > 22 && icModel < IcModels.Length;

    /// <summary>Lista de efeitos indexada pelo número do modo (0..121).</summary>
    public static readonly IReadOnlyList<string> ModeNames = BuildModeNames();

    private static string[] BuildModeNames()
    {
        var a = new string[ModeStatic + 1];
        a[ModeAuto] = "Auto (cycle all)";
        for (int i = 1; i < ModeStatic; i++) a[i] = $"Effect {i}";
        a[ModeStatic] = "Static color";
        return a;
    }

    private static byte B(int v) => (byte)Math.Clamp(v, 0, 255);
    private static byte[] Cmd(byte cmd, byte d0 = 0, byte d1 = 0, byte d2 = 0) => new[] { d0, d1, d2, cmd };

    public static byte[] PowerOn() => Cmd(CmdOn);
    public static byte[] PowerOff() => Cmd(CmdOff);
    public static byte[] Color(RgbColor c) => Cmd(CmdColor, c.R, c.G, c.B);
    public static byte[] Brightness(int v) => Cmd(CmdBrightness, B(v));
    public static byte[] White(int v) => Cmd(CmdWhite, B(v));
    public static byte[] Speed(int v) => Cmd(CmdSpeed, B(v));
    public static byte[] GetInfo() => Cmd(CmdGetInfo);

    public static byte[] Mode(int mode)
    {
        if (mode < 0 || mode > ModeStatic) throw new ArgumentOutOfRangeException(nameof(mode));
        return mode == ModeAuto ? Cmd(CmdModeAuto) : Cmd(CmdMode, (byte)mode);
    }

    public static byte[] IcModel(int index)
    {
        if (index < 0 || index >= IcModels.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return Cmd(CmdIcModel, (byte)index);
    }

    public static byte[] RgbOrder(int index)
    {
        if (index < 0 || index >= RgbOrders.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return Cmd(CmdRgbOrder, (byte)index);
    }

    /// <summary>Número de LEDs, big-endian: [MSB, LSB, 0, 0x2D].</summary>
    public static byte[] PixelCount(int n)
    {
        if (n < 1 || n > MaxPixels) throw new ArgumentOutOfRangeException(nameof(n));
        return Cmd(CmdPixelCount, (byte)(n >> 8), (byte)n);
    }

    /// <summary>
    /// "Verificar dispositivo" com 3 bytes aleatórios. Algumas versões de firmware
    /// cortam a ligação se não o receberem logo a seguir a ligar; responde com o estado.
    /// </summary>
    public static byte[] CheckDevice(Random? rng = null)
    {
        rng ??= Random.Shared;
        return Cmd(CmdCheckDevice, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
    }

    /// <summary>Muda o nome anunciado por Bluetooth (ASCII, até 20 caracteres).</summary>
    public static byte[] Rename(string name)
    {
        var ascii = new string(name.Where(ch => ch >= 0x20 && ch < 0x7F).Take(20).ToArray());
        if (ascii.Length == 0) throw new ArgumentException("Nome vazio", nameof(name));
        var b = new byte[4 + ascii.Length];
        b[0] = (byte)ascii.Length;
        b[3] = CmdRename;
        for (int i = 0; i < ascii.Length; i++) b[4 + i] = (byte)ascii[i];
        return b;
    }

    /// <summary>
    /// Interpreta uma notificação de estado. GET_INFO devolve 12 bytes;
    /// CHECK_DEVICE devolve 13 (o primeiro é um checksum, ignorado).
    /// </summary>
    public static Sp110eStatus? ParseStatus(ReadOnlySpan<byte> d)
    {
        if (d.Length == 13) d = d[1..];
        else if (d.Length != 12) return null;
        return new Sp110eStatus(
            IsOn: d[0] != 0,
            Mode: d[1],
            Speed: d[2],
            Brightness: d[3],
            IcModel: d[4],
            RgbOrder: d[5],
            PixelCount: (d[6] << 8) | d[7],
            Color: new RgbColor(d[8], d[9], d[10]),
            White: d[11]);
    }
}
