using Pine3LightControl.Core.Lights;
using Pine3LightControl.Core.PcRgb;
using Xunit;

sealed class FakeHid : IHidLink
{
    public readonly List<byte[]> Writes = new();
    public int Reads;
    public void Write(byte[] report) { Assert.Equal(65, report.Length); lock (Writes) Writes.Add(report); }
    public byte[]? Read(int timeoutMs) { Reads++; return new byte[65]; }
    public void Dispose() { }
}

/// <summary>SMBus falso com módulos Corsair que se comportam como os do CorsairDRAMController.</summary>
sealed class FakeSmbus : ISmbus
{
    public sealed class Module
    {
        public byte Reg43 = 0x1A, Reg44 = 0x04;
        public byte[] Info = new byte[32];
        public bool BadInfoCrc;
        public bool BlockFails;
        public readonly List<byte> Buffer = new();
        public int InfoPos;
        public bool InfoSelected;
        public readonly List<(byte Reg, byte[] Data)> Blocks = new();
        public readonly List<(byte Reg, ushort Word)> Words = new();
        public readonly List<byte> AppliedConfigs = new();
    }

    public readonly Dictionary<byte, Module> Modules = new();
    public readonly List<(byte Addr, byte Cmd, int Value)> ByteWrites = new();
    public readonly HashSet<byte> ProbedAddresses = new();

    public static Module Vengeance(ushort pid = 0x0100, byte protocol = 4)
    {
        var m = new Module();
        m.Info[0] = 0x1B; m.Info[1] = 0x1C;         // VID
        m.Info[2] = (byte)pid; m.Info[3] = (byte)(pid >> 8);
        m.Info[8] = 2; m.Info[9] = 1; m.Info[10] = 5;   // firmware 1.2.5
        m.Info[28] = protocol;
        return m;
    }

    public int ReadByteData(byte addr, byte command)
    {
        ProbedAddresses.Add(addr);
        if (!Modules.TryGetValue(addr, out var m)) return -1;
        switch (command)
        {
            case 0x43: return m.Reg43;
            case 0x44: return m.Reg44;
            case CorsairDram.RegGetBinaryData: return m.InfoSelected && m.InfoPos < 32 ? m.Info[m.InfoPos++] : 0;
            case CorsairDram.RegGetChecksum:
                var crc = m.InfoSelected ? Crc8.Compute(m.Info) : Crc8.Compute(m.Buffer.ToArray());
                return m.InfoSelected && m.BadInfoCrc ? crc ^ 0xFF : crc;
            case CorsairDram.RegStatus: return 0;
            default: return 0;
        }
    }

    public int WriteByteData(byte addr, byte command, byte value)
    {
        ByteWrites.Add((addr, command, value));
        if (!Modules.TryGetValue(addr, out var m)) return -1;
        switch (command)
        {
            case CorsairDram.RegGetDeviceInfo: m.InfoSelected = true; m.InfoPos = 0; break;
            case CorsairDram.RegResetBuffer: m.InfoSelected = false; m.Buffer.Clear(); break;
            case CorsairDram.RegSetBinaryData: m.Buffer.Add(value); break;
            case CorsairDram.RegWriteConfiguration: m.AppliedConfigs.Add(value); break;
        }
        return 0;
    }

    public int WriteWordData(byte addr, byte command, ushort value)
    {
        Modules[addr].Words.Add((command, value));
        return 0;
    }

    public int WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data)
    {
        var m = Modules[addr];
        if (m.BlockFails) return -1;
        Assert.True(data.Length <= 32);
        m.Blocks.Add((command, data.ToArray()));
        return 0;
    }

    public void Dispose() { }
}

public class PcRgbTests
{
    [Fact]
    public void Crc8_matches_crc8_smbus_check_value() =>
        Assert.Equal(0xF4, Crc8.Compute("123456789"u8));

    [Fact]
    public void Radeon_packets_match_openrgb_layout()
    {
        Assert.Equal(new byte[] { 0x00, 0x41, 0x43 }, Cmr6000.Off()[..3]);
        Assert.Equal(new byte[] { 0x00, 0x51, 0x28, 0x00, 0x00, 0xE0, 0x00 }, Cmr6000.Apply()[..7]);

        var e = Cmr6000.Effect(Cmr6000.ModeDirect, Cmr6000.SpeedDirect, 0, 200, new RgbColor(1, 2, 3));
        Assert.Equal(65, e.Length);
        Assert.Equal(new byte[] { 0x00, 0x51, 0x2C, 0x01, 0x00, 0x00, 0xFF, 0x00, 0xFF, 0xFF, 200, 1, 2, 3, 0, 0, 0, 0xFF }, e[..18]);
        Assert.All(e[0x11..], b => Assert.Equal(0xFF, b));

        var cyc = Cmr6000.Effect(Cmr6000.ModeColorCycle, Cmr6000.SpeedCycleNormal, 0, 255, new RgbColor(1, 2, 3));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, cyc[0x0B..0x0E]);
        Assert.Equal(0x03, Cmr6000.Effect(Cmr6000.ModeBreathe, 0x31, 0, 255, default)[0x08]);

        var cfg = Cmr6000.ColourConfig(0x02);
        Assert.Equal(new byte[] { 0x00, 0x51, 0xA0, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x05, 0x06 }, cfg[..11]);
        Assert.All(cfg[0x0B..0x1A], b => Assert.Equal(0x02, b));
        Assert.Equal(0, cfg[0x1A]);
    }

    [Fact]
    public async Task Radeon_light_sends_full_sequence()
    {
        var hid = new FakeHid();
        var gpu = new RadeonLedLight(hid, Cmr6000.Pid6000);
        gpu.SetColor(new RgbColor(0, 0, 255));
        await gpu.WhenSent();
        var w = hid.Writes;
        Assert.Equal(new byte[] { 0x41, 0x80 }, w[0][1..3]);          // enable
        Assert.Equal(0x2C, w[1][2]);                                  // efeito (sem segunda cor no 014D)
        Assert.Equal(new byte[] { 0, 0, 255 }, w[1][0x0B..0x0E]);
        Assert.Equal(0xA0, w[2][2]);                                  // colour config
        Assert.Equal(0x28, w[3][2]);                                  // apply
        Assert.Equal(3, hid.Reads);

        hid.Writes.Clear();
        gpu.SetPower(false);
        await gpu.WhenSent();
        Assert.Single(hid.Writes);
        Assert.Equal(0x43, hid.Writes[0][2]);

        hid.Writes.Clear();
        gpu.SetEffect(2);
        await gpu.WhenSent();
        Assert.Equal(Cmr6000.ModeColorCycle, hid.Writes[1][5]);
        Assert.Equal(Cmr6000.SpeedCycleNormal, hid.Writes[1][6]);

        // 6900 XT: envia também a segunda cor
        var hid2 = new FakeHid();
        var g2 = new RadeonLedLight(hid2, Cmr6000.Pid6900);
        g2.SetBrightness(100);
        await g2.WhenSent();
        Assert.Equal(0x9C, hid2.Writes[1][2]);
        Assert.Equal(100, hid2.Writes[2][0x0A]);
    }

    [Fact]
    public void Detects_only_valid_vengeance_pro_modules_with_reads_first()
    {
        var bus = new FakeSmbus();
        bus.Modules[0x58] = FakeSmbus.Vengeance();
        bus.Modules[0x59] = FakeSmbus.Vengeance(pid: 0x0101, protocol: 3);
        bus.Modules[0x5A] = FakeSmbus.Vengeance(pid: 0x0600);           // DDR5: não suportado
        bus.Modules[0x5B] = FakeSmbus.Vengeance(); bus.Modules[0x5B].BadInfoCrc = true;
        bus.Modules[0x5C] = FakeSmbus.Vengeance(); bus.Modules[0x5C].Reg43 = 0x55;   // não é Corsair
        var log = new List<string>();

        var found = CorsairDram.Detect(bus, log.Add, _ => { });

        Assert.Equal(new byte[] { 0x58, 0x59 }, found.Select(m => m.Address));
        Assert.Equal("1.2.5", found[0].Firmware);
        Assert.Equal(4, found[0].Protocol);
        Assert.Equal(3, found[1].Protocol);
        Assert.Equal(2, log.Count);
        // Só endereços DDR4 (nunca os sensores de temperatura 0x18–0x1F)
        Assert.All(bus.ProbedAddresses, a => Assert.InRange(a, (byte)0x58, (byte)0x5F));
        // Nenhuma escrita em módulos que não passaram nos registos 0x43/0x44
        Assert.DoesNotContain(bus.ByteWrites, w => w.Addr == 0x5C);
        // Só escritas de seleção de buffer durante a deteção
        Assert.All(bus.ByteWrites, w => Assert.Contains(w.Cmd, new byte[] { CorsairDram.RegGetDeviceInfo, CorsairDram.RegBinaryStart }));
    }

    [Fact]
    public void Direct_packet_and_block_or_word_writes()
    {
        var p = CorsairDram.DirectPacket(10, new RgbColor(1, 2, 3));
        Assert.Equal(32, p.Length);
        Assert.Equal(10, p[0]);
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3 }, p[1..7]);
        Assert.Equal(Crc8.Compute(p.AsSpan(0, 31)), p[31]);

        var bus = new FakeSmbus();
        bus.Modules[0x58] = FakeSmbus.Vengeance();
        var m = new CorsairDramModule(0x58, 0x1C1B, 0x0100, 4, "1", 10);
        CorsairDram.SetDirect(bus, m, new RgbColor(1, 2, 3));
        var blk = Assert.Single(bus.Modules[0x58].Blocks);   // 32 bytes cabem num bloco
        Assert.Equal(CorsairDram.RegColorBlock1, blk.Reg);
        Assert.Equal(p, blk.Data);
        Assert.Empty(bus.Modules[0x58].AppliedConfigs);    // modo direto não grava configuração

        bus.Modules[0x58].BlockFails = true;
        CorsairDram.SetDirect(bus, m, new RgbColor(1, 2, 3));
        var words = bus.Modules[0x58].Words;
        Assert.Equal(0x90 | (p[2] & 0x0F), words[0].Reg);
        Assert.Equal((ushort)(p[0] | (p[1] << 8)), words[0].Word);
        Assert.Equal(0xA0 | ((p[2] & 0xF0) >> 4), words[1].Reg);
        Assert.Equal((ushort)(p[3] | (p[4] << 8)), words[1].Word);
    }

    [Fact]
    public async Task Ram_light_uses_direct_mode_and_legacy_effect_only_with_valid_crc()
    {
        var bus = new FakeSmbus();
        bus.Modules[0x58] = FakeSmbus.Vengeance();
        bus.Modules[0x59] = FakeSmbus.Vengeance(protocol: 3);
        var modules = CorsairDram.Detect(bus, null, _ => { });
        using var ram = new CorsairRamLight(bus, modules);
        Assert.Equal("Memory ×2", ram.Name);
        Assert.Contains("legacy", ram.StatusText);

        ram.SetColor(new RgbColor(0, 255, 0));
        ram.SetBrightness(128);
        await ram.WhenSent();
        var last = bus.Modules[0x58].Blocks.Last().Data;
        Assert.Equal(new byte[] { 0, 128, 0 }, last[1..4]);            // verde a 50 %
        Assert.Equal(new byte[] { CorsairDram.IdEffectConfiguration, CorsairDram.IdColorData },
            bus.Modules[0x59].AppliedConfigs.TakeLast(2));               // firmware antigo: efeito + cores

        ram.SetPower(false);
        await ram.WhenSent();
        Assert.Equal(new byte[] { 0, 0, 0 }, bus.Modules[0x58].Blocks.Last().Data[1..4]);
    }
}
