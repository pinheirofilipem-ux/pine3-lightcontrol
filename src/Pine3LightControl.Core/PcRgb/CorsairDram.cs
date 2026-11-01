using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.PcRgb;

/// <summary>Módulo de memória Corsair encontrado no SMBus.</summary>
public sealed record CorsairDramModule(byte Address, ushort Vid, ushort Pid, byte Protocol, string Firmware, int LedCount);

/// <summary>
/// Memórias Corsair Vengeance RGB Pro DDR4 por SMBus. Protocolo do OpenRGB
/// (Controllers/CorsairDRAMController, release_1.1), com salvaguardas extra:
/// só procura nos endereços DDR4 (0x58–0x5F), a deteção só lê, e só aceita
/// módulos cuja informação de dispositivo tem o CRC certo e um PID conhecido.
/// </summary>
public static class CorsairDram
{
    public const byte RegResetBuffer = 0x0B;
    public const byte RegSetBinaryData = 0x20;
    public const byte RegBinaryStart = 0x21;
    public const byte RegStatus = 0x30;
    public const byte RegColorBlock1 = 0x31;
    public const byte RegColorBlock2 = 0x32;
    public const byte RegGetBinaryData = 0x40;
    public const byte RegGetChecksum = 0x42;
    public const byte RegGetDeviceInfo = 0x61;
    public const byte RegWriteConfiguration = 0x82;
    public const byte IdEffectConfiguration = 1;
    public const byte IdColorData = 2;
    public const byte ModeStatic = 0x10;

    /// <summary>Vengeance RGB Pro DDR4 (10 LEDs, ordem normal).</summary>
    public static readonly ushort[] VengeanceProDdr4Pids = { 0x0100, 0x0101 };
    public const int VengeanceProLeds = 10;

    /// <summary>Procura módulos. Só leituras até um módulo ser confirmado.</summary>
    public static List<CorsairDramModule> Detect(ISmbus bus, Action<string>? log = null, Action<int>? sleep = null)
    {
        sleep ??= ms => Thread.Sleep(ms);
        var found = new List<CorsairDramModule>();
        for (byte addr = 0x58; addr <= 0x5F; addr++)
        {
            int r = bus.ReadByteData(addr, 0x43);
            if (r is 0x1A or 0x1B or 0x1C)
            {
                int r2 = bus.ReadByteData(addr, 0x44);
                if (r2 is 0x01 or 0x03 or 0x04)
                {
                    var m = ReadDeviceInfo(bus, addr, log);
                    if (m is not null) found.Add(m);
                }
            }
            sleep(10);
        }
        return found;
    }

    private static CorsairDramModule? ReadDeviceInfo(ISmbus bus, byte addr, Action<string>? log)
    {
        // Seleciona o buffer de informação e lê-o (escritas só em registos de controlo)
        bus.WriteByteData(addr, RegGetDeviceInfo, 0x00);
        bus.WriteByteData(addr, RegBinaryStart, 0x00);
        var info = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            int v = bus.ReadByteData(addr, RegGetBinaryData);
            if (v < 0) return null;
            info[i] = (byte)v;
        }
        int crc = bus.ReadByteData(addr, RegGetChecksum);
        if (crc != Crc8.Compute(info))
        {
            log?.Invoke($"0x{addr:X2}: device info CRC mismatch — ignored");
            return null;
        }
        ushort vid = (ushort)(info[0] | (info[1] << 8));
        ushort pid = (ushort)(info[2] | (info[3] << 8));
        if (!VengeanceProDdr4Pids.Contains(pid))
        {
            log?.Invoke($"0x{addr:X2}: Corsair DRAM PID 0x{pid:X4} not supported — ignored");
            return null;
        }
        var fw = $"{info[9]}.{info[8]}.{info[10] | (info[11] << 8)}";
        return new CorsairDramModule(addr, vid, pid, info[28], fw, VengeanceProLeds);
    }

    /// <summary>
    /// Pacote do modo direto (protocolo ≥ 4): [n, R,G,B × n, CRC8]. Não grava nada
    /// de forma permanente no módulo.
    /// </summary>
    public static byte[] DirectPacket(int leds, RgbColor c)
    {
        var p = new byte[leds * 3 + 2];
        p[0] = (byte)leds;
        for (int i = 0; i < leds; i++)
        {
            p[1 + i * 3] = c.R;
            p[2 + i * 3] = c.G;
            p[3 + i * 3] = c.B;
        }
        p[^1] = Crc8.Compute(p.AsSpan(0, p.Length - 1));
        return p;
    }

    /// <summary>Como CorsairDRAMController::SetColorsPerLED em modo direto.</summary>
    public static void SetDirect(ISmbus bus, CorsairDramModule m, RgbColor c)
    {
        var p = DirectPacket(m.LedCount, c);
        int ret = bus.WriteBlockData(m.Address, RegColorBlock1, p.AsSpan(0, Math.Min(32, p.Length)));
        if (ret >= 0)
        {
            if (p.Length > 32) bus.WriteBlockData(m.Address, RegColorBlock2, p.AsSpan(32));
            return;
        }
        // Sem escrita em bloco: o mesmo pacote em palavras (0x90/0xA0 + nibbles), como o OpenRGB
        int idx = 0;
        while (idx < p.Length)
        {
            byte reg0 = idx == 0 ? (byte)0x90 : (byte)0xA0, reg1 = 0;
            ushort w0 = p[idx++], w1 = 0;
            if (idx < p.Length) w0 |= (ushort)(p[idx++] << 8);
            if (idx < p.Length)
            {
                reg1 = 0xA0;
                reg0 |= (byte)(p[idx] & 0x0F);
                reg1 |= (byte)((p[idx] & 0xF0) >> 4);
                idx++;
            }
            if (idx < p.Length) w1 = p[idx++];
            if (idx < p.Length) w1 |= (ushort)(p[idx++] << 8);
            bus.WriteWordData(m.Address, reg0, w0);
            if (reg1 > 0) bus.WriteWordData(m.Address, reg1, w1);
        }
    }

    /// <summary>
    /// Firmware antigo (protocolo &lt; 4, sem modo direto): efeito "estático" com a cor,
    /// aplicado só se o CRC calculado pelo módulo bater certo. Isto grava a
    /// configuração no módulo, por isso só é chamado com o valor final.
    /// </summary>
    public static bool SetStaticEffect(ISmbus bus, CorsairDramModule m, RgbColor c, byte brightness)
    {
        var data = new byte[20];
        data[0] = ModeStatic; data[1] = 0; data[2] = 1; data[3] = 0;
        data[4] = c.R; data[5] = c.G; data[6] = c.B; data[7] = brightness;
        data[8] = c.R; data[9] = c.G; data[10] = c.B; data[11] = brightness;
        if (!WriteBuffer(bus, m.Address, data, IdEffectConfiguration)) return false;
        var colors = new byte[m.LedCount * 4];
        for (int i = 0; i < m.LedCount; i++)
        {
            colors[i * 4] = c.R; colors[i * 4 + 1] = c.G; colors[i * 4 + 2] = c.B; colors[i * 4 + 3] = 0xFF;
        }
        return WriteBuffer(bus, m.Address, colors, IdColorData);
    }

    private static bool WriteBuffer(ISmbus bus, byte addr, byte[] data, byte bufferId)
    {
        bus.WriteByteData(addr, RegResetBuffer, 0x00);
        bus.WriteByteData(addr, RegBinaryStart, 0x00);
        foreach (var b in data) bus.WriteByteData(addr, RegSetBinaryData, b);
        if (bus.ReadByteData(addr, RegGetChecksum) != Crc8.Compute(data)) return false;   // não aplica
        bus.WriteByteData(addr, RegWriteConfiguration, bufferId);
        for (int i = 0; i < 5; i++)
        {
            int st = bus.ReadByteData(addr, RegStatus);
            if (st >= 0 && (st & 0x08) == 0) break;
            Thread.Sleep(10);
        }
        return true;
    }
}

/// <summary>Todos os módulos de memória como uma luz: on/off, brilho (cor escalada) e cor.</summary>
public sealed class CorsairRamLight : LightBase, IDisposable
{
    private readonly ISmbus _bus;
    private readonly IReadOnlyList<CorsairDramModule> _modules;
    private readonly SendQueue _queue;
    private string _status;

    public CorsairRamLight(ISmbus bus, IReadOnlyList<CorsairDramModule> modules)
    {
        _bus = bus;
        _modules = modules;
        bool legacy = modules.Any(m => m.Protocol < 4);
        // Firmware antigo grava no módulo: só o valor final (pausa longa entre envios)
        _queue = new SendQueue(TimeSpan.FromMilliseconds(legacy ? 800 : 40));
        _status = $"{modules.Count} module(s), firmware {modules[0].Firmware}" + (legacy ? " (legacy mode)" : "");
        IsOn = true;
        Color = RgbColor.White;
        _queue.Error += e =>
        {
            _status = "Error: " + e.Message;
            Log.Write($"[RAM] {e.Message}");
            RaiseChanged();
        };
    }

    public IReadOnlyList<CorsairDramModule> Modules => _modules;
    public override string Id => "pc:ram";
    public override string Name => _modules.Count > 1 ? $"Memory ×{_modules.Count}" : "Memory";
    public override LightSource Source => LightSource.Pc;
    public override LightFeatures Features => LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color;
    public override bool IsAvailable => true;
    public override string StatusText => _status;

    private void Push()
    {
        var c = IsOn
            ? new RgbColor((byte)(Color.R * Brightness / 255), (byte)(Color.G * Brightness / 255), (byte)(Color.B * Brightness / 255))
            : new RgbColor(0, 0, 0);
        _queue.Post("leds", () =>
        {
            foreach (var m in _modules)
            {
                if (m.Protocol >= 4) CorsairDram.SetDirect(_bus, m, c);
                else CorsairDram.SetStaticEffect(_bus, m, c, 255);
            }
            return Task.CompletedTask;
        });
    }

    public override void SetPower(bool on) { IsOn = on; Push(); RaiseChanged(); }
    public override void SetBrightness(int v) { Brightness = Math.Clamp(v, 1, 255); IsOn = true; Push(); RaiseChanged(); }
    public override void SetColor(RgbColor c) { Color = c; IsOn = true; Push(); RaiseChanged(); }

    public Task WhenSent() => _queue.WhenIdle();
    public void Dispose() => _bus.Dispose();
}
