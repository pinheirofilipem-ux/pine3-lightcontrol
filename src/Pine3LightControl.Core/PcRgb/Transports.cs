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

/// <summary>
/// Embrulha um IHidLink e regista no log os primeiros relatórios enviados e
/// recebidos (diagnóstico da deteção). Depois de <c>Limit</c> linhas fica calado.
/// </summary>
public sealed class LoggingHid : IHidLink
{
    private readonly IHidLink _inner;
    private readonly string _tag;
    private int _lines;
    public int Limit { get; set; } = 60;

    public LoggingHid(IHidLink inner, string tag)
    {
        _inner = inner;
        _tag = tag;
    }

    private void Line(string dir, byte[]? data)
    {
        if (_lines++ >= Limit) return;
        Log.Write($"[{_tag}] {dir} {(data is null ? "(timeout)" : Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 24))))}");
    }

    public void Write(byte[] report)
    {
        Line(">>", report);
        _inner.Write(report);
    }

    public byte[]? Read(int timeoutMs)
    {
        var r = _inner.Read(timeoutMs);
        Line("<<", r);
        return r;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Duas coleções do mesmo dispositivo (ex.: recetor Logitech): escreve na principal e
/// lê das duas — os erros HID++ 1.0 chegam pela coleção curta.
/// </summary>
public sealed class PairedHid : IHidLink
{
    private readonly IHidLink _main;
    private readonly IHidLink? _side;

    public PairedHid(IHidLink main, IHidLink? side)
    {
        _main = main;
        _side = side;
    }

    public void Write(byte[] report) => _main.Write(report);

    public byte[]? Read(int timeoutMs)
    {
        if (_side is null) return _main.Read(timeoutMs);
        var r = _main.Read(Math.Max(timeoutMs - 60, 20));
        return r ?? _side.Read(60);
    }

    public void Dispose()
    {
        _main.Dispose();
        _side?.Dispose();
    }
}
