using System.Buffers.Binary;
using System.Text;

namespace Pine3LightControl.Core.OpenRgb;

/// <summary>
/// Protocolo SDK do OpenRGB (TCP 6742). Cabeçalho de 16 bytes: "ORGB", dev_id,
/// pkt_id, pkt_size (uint32 little-endian). Usamos a versão 1 do protocolo, que
/// o servidor continua a servir e é a mais simples (sem segmentos nem IDs).
/// Formato confirmado em OpenRGB release_1.1: NetworkProtocol.h, NetworkServer.cpp,
/// RGBController.cpp (GetDeviceDescriptionData e afins).
/// </summary>
public static class OpenRgbProtocol
{
    public const int DefaultPort = 6742;
    public const uint ClientVersion = 1;
    public const int HeaderSize = 16;

    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdated = 100;
    public const uint UpdateLeds = 1050;
    public const uint SetCustomMode = 1100;

    public static byte[] Packet(uint devId, uint pktId, ReadOnlySpan<byte> payload)
    {
        var b = new byte[HeaderSize + payload.Length];
        b[0] = (byte)'O'; b[1] = (byte)'R'; b[2] = (byte)'G'; b[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), devId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), pktId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    public static byte[] UInt32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    /// <summary>Payload de UPDATELEDS: uint32 tamanho total, uint16 n, n × (R, G, B, 0).</summary>
    public static byte[] UpdateLedsPayload(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        int size = 4 + 2 + 4 * colors.Count;
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)colors.Count);
        for (int i = 0; i < colors.Count; i++)
        {
            b[6 + i * 4] = colors[i].R;
            b[7 + i * 4] = colors[i].G;
            b[8 + i * 4] = colors[i].B;
        }
        return b;
    }

    /// <summary>
    /// Lê a resposta a REQUEST_CONTROLLER_DATA (uint32 tamanho + descrição) para
    /// versões 0..3 do protocolo.
    /// </summary>
    public static OpenRgbDevice ParseController(uint index, ReadOnlySpan<byte> data, uint version)
    {
        if (version > 3) throw new NotSupportedException($"protocol {version}");
        var r = new Reader(data);
        r.U32();                                   // tamanho total
        var type = r.I32();
        var name = r.Str();
        var vendor = version >= 1 ? r.Str() : "";
        var description = r.Str();
        r.Str();                                   // versão
        var serial = r.Str();
        var location = r.Str();

        int numModes = r.U16();
        int activeMode = r.I32();
        var modes = new List<string>(numModes);
        for (int m = 0; m < numModes; m++)
        {
            modes.Add(r.Str());
            r.I32();                               // value
            r.U32();                               // flags
            r.U32(); r.U32();                      // speed min / max
            if (version >= 3) { r.U32(); r.U32(); } // brightness min / max
            r.U32(); r.U32();                      // colors min / max
            r.U32();                               // speed
            if (version >= 3) r.U32();             // brightness
            r.U32();                               // direction
            r.U32();                               // color mode
            int n = r.U16();
            r.Skip(4 * n);
        }

        int numZones = r.U16();
        var zones = new List<string>(numZones);
        for (int z = 0; z < numZones; z++)
        {
            zones.Add(r.Str());
            r.U32();                               // type
            r.U32(); r.U32(); r.U32();             // leds min / max / count
            int matrix = r.U16();                  // tamanho do mapa em bytes (0 = sem mapa)
            r.Skip(matrix);
        }

        int numLeds = r.U16();
        for (int l = 0; l < numLeds; l++)
        {
            r.Str();
            r.U32();                               // value
        }

        int numColors = r.U16();
        var colors = new (byte R, byte G, byte B)[numColors];
        for (int c = 0; c < numColors; c++)
        {
            var px = r.Bytes(4);
            colors[c] = (px[0], px[1], px[2]);
        }

        return new OpenRgbDevice(index, type, name, vendor, description, serial, location,
            modes, activeMode, zones, numLeds, colors);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _p;
        public Reader(ReadOnlySpan<byte> d) { _d = d; _p = 0; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _p + n > _d.Length) throw new FormatException("OpenRGB controller data truncated");
            var s = _d.Slice(_p, n);
            _p += n;
            return s;
        }

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public ReadOnlySpan<byte> Bytes(int n) => Take(n);
        public void Skip(int n) => Take(n);

        /// <summary>uint16 comprimento (com o \0) + bytes.</summary>
        public string Str()
        {
            int len = U16();
            var b = Take(len);
            if (len > 0 && b[len - 1] == 0) b = b[..(len - 1)];
            return Encoding.UTF8.GetString(b);
        }
    }
}

/// <summary>Um controlador RGB tal como o OpenRGB o descreve.</summary>
public sealed record OpenRgbDevice(
    uint Index, int Type, string Name, string Vendor, string Description, string Serial, string Location,
    IReadOnlyList<string> Modes, int ActiveMode, IReadOnlyList<string> Zones, int LedCount,
    IReadOnlyList<(byte R, byte G, byte B)> Colors)
{
    /// <summary>Tipos de dispositivo do OpenRGB (RGBControllerInterface.h).</summary>
    public string TypeName => Type switch
    {
        0 => "Motherboard", 1 => "Memory", 2 => "Graphics card", 3 => "Cooler", 4 => "LED strip",
        5 => "Keyboard", 6 => "Mouse", 7 => "Mouse mat", 8 => "Headset", 9 => "Headset stand",
        10 => "Gamepad", 11 => "Light", 12 => "Speaker", 13 => "Virtual", 14 => "Storage",
        15 => "Case", 16 => "Microphone", 17 => "Accessory", 18 => "Keypad",
        _ => "Device",
    };
}
