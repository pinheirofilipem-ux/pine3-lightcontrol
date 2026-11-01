namespace Pine3LightControl.Core.PcRgb;

/// <summary>
/// Dispositivo HID (relatórios de 65 bytes: o primeiro é o report ID 0x00).
/// Implementado na app com HidSharp; nos testes por um falso.
/// </summary>
public interface IHidLink : IDisposable
{
    void Write(byte[] report);
    /// <summary>Lê uma resposta (ou null ao fim do tempo).</summary>
    byte[]? Read(int timeoutMs);
}

/// <summary>
/// Barramento SMBus (como i2c_smbus_interface do OpenRGB). Devolve &lt; 0 em erro.
/// Implementado na app com o PawnIO (módulo SmbusI801).
/// </summary>
public interface ISmbus : IDisposable
{
    int ReadByteData(byte addr, byte command);
    int WriteByteData(byte addr, byte command, byte value);
    int WriteWordData(byte addr, byte command, ushort value);
    int WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data);
}

/// <summary>CRC-8 (polinómio 0x07, init 0, sem reflexão) — o CRCPP::CRC_8 do OpenRGB.</summary>
public static class Crc8
{
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }
}
