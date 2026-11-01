using Pine3LightControl.Core.Lights;
using Pine3LightControl.Core.PcRgb;
using Xunit;

/// <summary>HID falso com respostas calculadas a partir do pedido.</summary>
sealed class ScriptedHid : IHidLink
{
    private readonly Func<byte[], byte[]?> _reply;
    private readonly Queue<byte[]> _pending = new();
    public readonly List<byte[]> Writes = new();
    public ScriptedHid(Func<byte[], byte[]?> reply) => _reply = reply;
    public void Write(byte[] report)
    {
        Writes.Add(report);
        if (_reply(report) is { } r) _pending.Enqueue(r);
    }
    public byte[]? Read(int timeoutMs) => _pending.Count > 0 ? _pending.Dequeue() : null;
    public void Dispose() { }
}

public class PeripheralTests
{
    // ── VIA ──
    private sealed class FakeVia
    {
        public readonly Dictionary<(byte Ch, byte Id), byte[]> Values = new()
        {
            [(3, 1)] = new byte[] { 200 }, [(3, 2)] = new byte[] { 4 }, [(3, 3)] = new byte[] { 100 }, [(3, 4)] = new byte[] { 0, 255 },
            [(2, 1)] = new byte[] { 50 }, [(2, 2)] = new byte[] { 0 }, [(2, 3)] = new byte[] { 10 }, [(2, 4)] = new byte[] { 170, 255 },
        };
        public int Version = 12;
        public byte[]? Reply(byte[] w)
        {
            Assert.Equal(Via.ReportSize, w.Length);
            Assert.Equal(0, w[0]);
            var d = w[1..];
            var r = new byte[33];
            switch (d[0])
            {
                case Via.CmdProtocolVersion: r[1] = 1; r[2] = (byte)(Version >> 8); r[3] = (byte)Version; break;
                case Via.CmdGetValue:
                    d.AsSpan(0, 3).CopyTo(r.AsSpan(1));
                    Values[(d[1], d[2])].CopyTo(r, 4);
                    break;
                case Via.CmdSetValue:
                    d.CopyTo(r, 1);
                    int n = d[2] == Via.ValColor ? 2 : 1;
                    Values[(d[1], d[2])] = d.AsSpan(3, n).ToArray();
                    break;
                default: r[1] = 0xFF; break;
            }
            return r;
        }
    }

    [Fact]
    public async Task Via_keyboard_reads_state_and_sets_values()
    {
        var kb = new FakeVia();
        var hid = new ScriptedHid(kb.Reply);
        var zones = DuckyOkM.Create(hid, out var problem)!;
        Assert.Equal("", problem);
        var keys = zones[0];
        var glow = zones[1];

        Assert.Equal("pc:keyboard-keys", keys.Id);
        Assert.True(keys.IsOn);
        Assert.Equal(4, keys.Effect);
        Assert.Equal("Cycle All", keys.Effects[keys.Effect]);
        Assert.Equal(200, keys.Brightness);
        Assert.Equal(100, keys.Speed);
        Assert.Equal(new RgbColor(255, 0, 0), keys.Color);       // hue 0, sat 255
        Assert.False(glow.IsOn);                                 // efeito 0 = All Off
        Assert.Equal(21, glow.Effects.Count);

        keys.SetColor(new RgbColor(0, 0, 255));
        keys.SetSpeed(30);
        keys.SetPower(false);
        await keys.WhenSent();
        Assert.Equal(new byte[] { 170, 255 }, kb.Values[(3, 4)]);  // azul = hue 240° → 170
        Assert.Equal(new byte[] { 30 }, kb.Values[(3, 3)]);
        Assert.Equal(new byte[] { 0 }, kb.Values[(3, 2)]);

        keys.SetPower(true);
        await keys.WhenSent();
        Assert.Equal(new byte[] { 4 }, kb.Values[(3, 2)]);         // volta ao último efeito

        glow.SetBrightness(128);                                    // ligar pelo brilho liga a zona
        await glow.WhenSent();
        Assert.Equal(new byte[] { 128 }, kb.Values[(2, 1)]);
        Assert.Equal(new byte[] { 1 }, kb.Values[(2, 2)]);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x09);       // nunca grava na EEPROM
    }

    [Fact]
    public void Via_rejects_old_protocol_and_silent_device()
    {
        var kb = new FakeVia { Version = 9 };
        Assert.Null(DuckyOkM.Create(new ScriptedHid(kb.Reply), out var p1));
        Assert.Contains("VIA protocol 9", p1);
        Assert.Null(DuckyOkM.Create(new ScriptedHid(_ => null), out var p2));
        Assert.Contains("did not answer", p2);
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 128, 0)]
    public void Hsv_round_trip(byte r, byte g, byte b)
    {
        var (h, s, _) = Via.ToHsv(new RgbColor(r, g, b));
        var back = Via.FromHsv(h, s);
        Assert.InRange(Math.Abs(back.R - r), 0, 4);
        Assert.InRange(Math.Abs(back.G - g), 0, 4);
        Assert.InRange(Math.Abs(back.B - b), 0, 4);
    }

    // ── Logitech HID++ ──
    private sealed class FakeReceiver
    {
        public const byte Dev = 1, Feature = 0x0B;
        public readonly List<byte[]> Effects = new();
        // 2 zonas; efeitos por índice: 0 off, 1 on, 2 spectrum (zona 0); zona 1 sem spectrum
        private readonly ushort[][] _fx = { new ushort[] { 0x0000, 0x0001, 0x0003, 0x000A }, new ushort[] { 0x0000, 0x0001, 0x000A } };

        public byte[]? Reply(byte[] w)
        {
            Assert.Equal(20, w.Length);
            Assert.Equal(0x11, w[0]);
            var r = new byte[20];
            w.AsSpan(0, 4).CopyTo(r);
            if (w[1] != Dev) return null;                            // nada noutro índice
            if (w[2] == 0x00 && w[3] == LogitechHidpp.CmdGetFeature)
            {
                r[4] = (w[4] << 8 | w[5]) == 0x8070 ? Feature : (byte)0;
                return r;
            }
            if (w[2] != Feature) return null;
            switch (w[3])
            {
                case LogitechHidpp.CmdGetCount: r[4] = 2; break;
                case LogitechHidpp.CmdGetInfo: r[4] = w[4]; r[5] = 0; r[6] = (byte)(w[4] + 1); r[7] = (byte)_fx[w[4]].Length; break;
                case LogitechHidpp.CmdGetControl: r[4] = w[4]; r[5] = w[5]; r[6] = (byte)(_fx[w[4]][w[5]] >> 8); r[7] = (byte)_fx[w[4]][w[5]]; break;
                case LogitechHidpp.CmdSetEffect: Effects.Add(w); break;
            }
            return r;
        }
    }

    [Fact]
    public async Task Logitech_mouse_detects_zones_and_sets_effects()
    {
        var rx = new FakeReceiver();
        var hid = new ScriptedHid(rx.Reply);
        var found = LogitechHidpp.Detect(hid);
        Assert.NotNull(found);
        var (dev, feature, zones, page) = found!.Value;
        Assert.Equal(LogitechHidpp.FeatureRgbEffects, page);
        Assert.Equal(FakeReceiver.Dev, dev);
        Assert.Equal(FakeReceiver.Feature, feature);
        Assert.Equal(2, zones.Count);
        Assert.Equal(2, zones[0].Effects[LogitechHidpp.FxSpectrum]);

        var mouse = new LogitechMouseLight(hid, dev, feature, zones);
        mouse.SetColor(new RgbColor(255, 0, 0));
        mouse.SetBrightness(128);
        await mouse.WhenSent();
        var last = rx.Effects.TakeLast(2).ToList();
        Assert.Equal(new byte[] { 0, 1, 128, 0, 0 }, last[0][4..9]);    // zona 0, efeito "on", cor escalada
        Assert.Equal(new byte[] { 1, 1, 128, 0, 0 }, last[1][4..9]);    // zona 1
        Assert.Equal(0, last[0][16]);                                    // sem persistência

        rx.Effects.Clear();
        mouse.SetEffect(1);                                              // breathing
        await mouse.WhenSent();
        Assert.Equal(3, rx.Effects[0][5]);                               // índice do breathing na zona 0
        Assert.Equal(new byte[] { 0x0B, 0xB8 }, rx.Effects[0][9..11]);  // 3000 ms
        Assert.Equal(50, rx.Effects[0][12]);                            // brilho 50 %

        rx.Effects.Clear();
        mouse.SetEffect(2);                                              // spectrum: zona 1 não tem
        await mouse.WhenSent();
        Assert.Single(rx.Effects);
        Assert.Contains("did not answer", mouse.StatusText);

        rx.Effects.Clear();
        mouse.SetPower(false);
        await mouse.WhenSent();
        Assert.All(rx.Effects, e => Assert.Equal(0, e[5]));             // efeito "off"
    }

    [Fact]
    public void Logitech_detect_returns_null_without_rgb_feature() =>
        Assert.Null(LogitechHidpp.Detect(new ScriptedHid(_ => null)));

    // 0x8071 (G903 HERO): sem 0x8070, zonas lidas pelo INFO, efeitos com 0x10 e CONTROL antes
    private sealed class FakeReceiver8071
    {
        public const byte Dev = 1, Feature = 0x0C, FeatureSetIdx = 0x01;
        public readonly List<byte[]> Sent = new();
        private readonly ushort[] _fx = { 0x0000, 0x0001, 0x0003, 0x000A };

        public byte[]? Reply(byte[] w)
        {
            var r = new byte[20];
            w.AsSpan(0, 4).CopyTo(r);
            if (w[1] != Dev) return new byte[] { 0x10, w[1], 0x8F, 0x00, 0x01, 0x08, 0x00 };   // erro HID++ 1.0
            if (w[2] == 0x00)
            {
                int pg = w[4] << 8 | w[5];
                r[4] = pg == 0x8071 ? Feature : pg == 0x0001 ? FeatureSetIdx : (byte)0;
                return r;
            }
            if (w[2] != Feature) return null;
            switch (w[3])
            {
                case LogitechHidpp.Cmd2Info when w[4] == 0xFF && w[5] == 0xFF: r[6] = 2; break;          // 2 zonas
                case LogitechHidpp.Cmd2Info when w[5] == 0xFF: r[4] = w[4]; r[7] = (byte)(w[4] + 1); r[8] = (byte)_fx.Length; break;
                case LogitechHidpp.Cmd2Info: r[4] = w[4]; r[5] = w[5]; r[6] = (byte)(_fx[w[5]] >> 8); r[7] = (byte)_fx[w[5]]; break;
                default: Sent.Add(w); break;
            }
            return r;
        }
    }

    [Fact]
    public async Task Logitech_8071_mouse_detects_and_takes_software_control()
    {
        var rx = new FakeReceiver8071();
        var hid = new ScriptedHid(rx.Reply);
        var found = LogitechHidpp.Detect(hid, out var note);
        Assert.NotNull(found);
        Assert.Null(note);
        var (dev, feature, zones, page) = found!.Value;
        Assert.Equal(LogitechHidpp.FeatureRgbEffects2, page);
        Assert.Equal(2, zones.Count);
        Assert.Equal(2, zones[1].Location);
        Assert.Equal(3, zones[0].Effects[LogitechHidpp.FxBreathing]);

        var mouse = new LogitechMouseLight(hid, dev, feature, zones, page: page);
        mouse.SetColor(new RgbColor(0, 255, 0));
        await mouse.WhenSent();
        Assert.Equal(LogitechHidpp.Cmd2Control, rx.Sent[0][3]);
        Assert.Equal(new byte[] { 1, 3, 5 }, rx.Sent[0][4..7]);
        var fx = rx.Sent[1];
        Assert.Equal(LogitechHidpp.Cmd2SetEffect, fx[3]);
        Assert.Equal(new byte[] { 0, 1, 0, 255, 0 }, fx[4..9]);
        Assert.Equal(1, fx[16]);
        mouse.SetEffect(1);
        await mouse.WhenSent();
        Assert.Single(rx.Sent, w => w[3] == LogitechHidpp.Cmd2Control);   // CONTROL só uma vez
    }

    [Fact]
    public void Logitech_detect_reports_features_when_no_rgb()
    {
        // Responde à raiz sem RGB; IFeatureSet com 2 funcionalidades
        var hid = new ScriptedHid(w =>
        {
            var r = new byte[20];
            w.AsSpan(0, 4).CopyTo(r);
            if (w[1] != 1) return null;
            if (w[2] == 0) { r[4] = (w[4] << 8 | w[5]) == 1 ? (byte)1 : (byte)0; return r; }
            if (w[3] == LogitechHidpp.CmdFeatureCount) r[4] = 2;
            else { r[4] = 0x10; r[5] = (byte)(w[4] == 1 ? 0x00 : 0x04); }
            return r;
        });
        Assert.Null(LogitechHidpp.Detect(hid, out var note));
        Assert.Contains("1000 1004", note);
    }

    [Fact]
    public void Via_accepts_64_byte_reports_with_report_id()
    {
        // OK-M 75: relatórios de 64 bytes + report ID = 65
        var hid = new ScriptedHid(w =>
        {
            var r = new byte[65];
            r[1] = w[1];
            if (w[1] == Via.CmdProtocolVersion) { r[2] = 0; r[3] = 12; }
            return r;
        });
        Assert.Equal(12, Via.ProtocolVersion(hid));
    }
}
