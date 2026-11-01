using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pine3LightControl.Core;
using Pine3LightControl.Core.Lights;
using Pine3LightControl.Core.OpenRgb;
using Xunit;

/// <summary>
/// Servidor SDK do OpenRGB falso. A serialização segue o servidor real (release_1.1,
/// RGBController.cpp: GetDeviceDescriptionData / Mode / Zone / LED / Color) para as
/// versões 0–3, e responde ao pedido de versão com 6, como o servidor 1.1.
/// </summary>
sealed class FakeOpenRgbServer : IAsyncDisposable
{
    public sealed record Dev(int Type, string Name, int Leds, (byte, byte, byte) Color, bool Matrix = false);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private Socket? _client;
    public List<Dev> Devices = new()
    {
        new(1, "Corsair Vengeance RGB Pro DDR4", 10, (255, 0, 0)),
        new(1, "Corsair Vengeance RGB Pro DDR4", 10, (255, 0, 0)),
        new(1, "Corsair Vengeance RGB Pro DDR4", 10, (255, 0, 0)),
        new(1, "Corsair Vengeance RGB Pro DDR4", 10, (255, 0, 0)),
        new(6, "G903 LIGHTSPEED Wireless Gaming Mouse w/ HERO", 2, (0, 0, 0)),
        new(2, "AMD RX 6xxx GPU", 1, (0, 0, 128), Matrix: true),
    };
    public readonly ConcurrentQueue<(uint Dev, uint Pkt, byte[] Data)> Received = new();
    public uint NegotiatedVersion;
    public int Connections;
    public string ClientName = "";

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeOpenRgbServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptSocketAsync(_cts.Token); } catch { return; }
            Interlocked.Increment(ref Connections);
            _client = s;
            _ = Task.Run(() => Serve(s));
        }
    }

    private async Task Serve(Socket s)
    {
        using var ns = new NetworkStream(s, ownsSocket: true);
        var hdr = new byte[16];
        try
        {
            while (true)
            {
                await ns.ReadExactlyAsync(hdr);
                Assert.Equal("ORGB", Encoding.ASCII.GetString(hdr, 0, 4));
                uint dev = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
                uint pkt = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
                var data = new byte[size];
                if (size > 0) await ns.ReadExactlyAsync(data);
                Received.Enqueue((dev, pkt, data));
                switch (pkt)
                {
                    case 40:
                        NegotiatedVersion = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), 6u);
                        await ns.WriteAsync(OpenRgbProtocol.Packet(0, 40, OpenRgbProtocol.UInt32(6)));
                        break;
                    case 50:
                        ClientName = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        break;
                    case 0:
                        await ns.WriteAsync(OpenRgbProtocol.Packet(0, 0, OpenRgbProtocol.UInt32((uint)Devices.Count)));
                        break;
                    case 1:
                        uint v = data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
                        await ns.WriteAsync(OpenRgbProtocol.Packet(dev, 1, Describe(Devices[(int)dev], v)));
                        break;
                    case 1050:
                        // Como o servidor real: o tamanho declarado tem de bater com o pacote
                        Assert.Equal(size, BinaryPrimitives.ReadUInt32LittleEndian(data));
                        break;
                }
            }
        }
        catch (Exception) { /* ligação fechada */ }
    }

    public async Task PushDeviceListUpdated()
    {
        if (_client is { Connected: true } c)
            await c.SendAsync(OpenRgbProtocol.Packet(0, 100, ReadOnlySpan<byte>.Empty).AsMemory(), SocketFlags.None);
    }

    public void DropClient() => _client?.Close();

    // ── Serialização (do lado do servidor) ──
    private static void Str(List<byte> b, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        U16(b, (ushort)bytes.Length);
        b.AddRange(bytes);
    }
    private static void U16(List<byte> b, ushort v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
    private static void U32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
    private static void Color(List<byte> b, (byte R, byte G, byte B) c) { b.Add(c.R); b.Add(c.G); b.Add(c.B); b.Add(0); }

    public static byte[] Describe(Dev d, uint v)
    {
        var b = new List<byte>();
        U32(b, (uint)d.Type);
        Str(b, d.Name);
        if (v >= 1) Str(b, "Vendor");
        Str(b, "desc"); Str(b, "1.0"); Str(b, "SN"); Str(b, "HID: /dev/x");
        // 2 modos: Direct (sem cores) e Breathing (2 cores)
        U16(b, 2);
        U32(b, 0);                       // active_mode
        foreach (var (name, colors) in new[] { ("Direct", 0), ("Breathing", 2) })
        {
            Str(b, name);
            U32(b, 1);                   // value
            U32(b, 0x20);                // flags
            U32(b, 0); U32(b, 100);      // speed min/max
            if (v >= 3) { U32(b, 0); U32(b, 100); }
            U32(b, 0); U32(b, (uint)colors);
            U32(b, 50);                  // speed
            if (v >= 3) U32(b, 100);
            U32(b, 0); U32(b, 1);        // direction, color_mode
            U16(b, (ushort)colors);
            for (int i = 0; i < colors; i++) Color(b, (9, 9, 9));
        }
        // 1 zona; a da gráfica tem mapa em matriz (2×1)
        U16(b, 1);
        Str(b, "Zone");
        U32(b, d.Matrix ? 2u : 1u);
        U32(b, (uint)d.Leds); U32(b, (uint)d.Leds); U32(b, (uint)d.Leds);
        if (d.Matrix) { U16(b, 8 + 4 * 2); U32(b, 2); U32(b, 1); U32(b, 0); U32(b, 0xFFFFFFFF); }
        else U16(b, 0);
        U16(b, (ushort)d.Leds);
        for (int i = 0; i < d.Leds; i++) { Str(b, $"LED {i}"); U32(b, (uint)i); }
        U16(b, (ushort)d.Leds);
        for (int i = 0; i < d.Leds; i++) Color(b, d.Color);
        var body = b.ToArray();
        var res = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(res, (uint)res.Length);
        body.CopyTo(res, 4);
        return res;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _client?.Close();
        _listener.Stop();
        await Task.CompletedTask;
    }
}

public class OpenRgbTests
{
    private static async Task Until(Func<bool> cond, int seconds = 5)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void Parses_controller_description(uint version)
    {
        var dev = new FakeOpenRgbServer.Dev(2, "AMD RX 6xxx GPU", 3, (1, 2, 3), Matrix: true);
        var parsed = OpenRgbProtocol.ParseController(5, FakeOpenRgbServer.Describe(dev, version), version);
        Assert.Equal(5u, parsed.Index);
        Assert.Equal("AMD RX 6xxx GPU", parsed.Name);
        Assert.Equal("Graphics card", parsed.TypeName);
        Assert.Equal(version >= 1 ? "Vendor" : "", parsed.Vendor);
        Assert.Equal(new[] { "Direct", "Breathing" }, parsed.Modes);
        Assert.Equal(3, parsed.LedCount);
        Assert.All(parsed.Colors, c => Assert.Equal(((byte)1, (byte)2, (byte)3), c));
        Assert.Throws<FormatException>(() => OpenRgbProtocol.ParseController(0, FakeOpenRgbServer.Describe(dev, version)[..20], version));
    }

    [Fact]
    public void Update_leds_payload_layout()
    {
        var p = OpenRgbProtocol.UpdateLedsPayload(new[] { ((byte)1, (byte)2, (byte)3), ((byte)4, (byte)5, (byte)6) });
        Assert.Equal(Convert.FromHexString("0E000000" + "0200" + "01020300" + "04050600"), p);
    }

    [Fact]
    public async Task Connects_groups_and_controls_devices()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "p3lc-test-" + Guid.NewGuid());
        await using var server = new FakeOpenRgbServer();
        var cfg = new AppConfig();
        cfg.OpenRgb.Enabled = true;
        cfg.OpenRgb.Host = "127.0.0.1";
        cfg.OpenRgb.Port = server.Port;
        cfg.OpenRgb.Devices.Add("Corsair Vengeance RGB Pro DDR4");
        await using var hub = new LightHub(cfg);
        await hub.StartAsync();

        await Until(() => hub.OpenRgb.State == OpenRgbState.Connected);
        Assert.Equal(1u, server.NegotiatedVersion);
        Assert.Equal("Pine3 Light Control", server.ClientName);
        Assert.Equal(6, hub.OpenRgb.Devices.Count);
        Assert.Contains(("AMD RX 6xxx GPU", "Graphics card", 1), hub.OpenRgbGroups);
        Assert.Contains(("Corsair Vengeance RGB Pro DDR4", "Memory", 4), hub.OpenRgbGroups);

        var ram = Assert.IsType<OpenRgbLight>(hub.Lights.Single());
        await Until(() => ram.IsAvailable);
        Assert.Equal("Corsair Vengeance RGB Pro DDR4 ×4", ram.Name);
        Assert.Equal("orgb:Corsair Vengeance RGB Pro DDR4", ram.Id);
        Assert.True(ram.IsOn);                                   // lido dos LEDs (vermelho)
        Assert.Equal(new RgbColor(255, 0, 0), ram.Color);

        server.Received.Clear();
        ram.SetColor(new RgbColor(0, 255, 0));
        ram.SetBrightness(128);
        await ram.WhenSent();
        var sent = server.Received.ToArray();
        // Primeira vez: modo Direct em cada módulo, depois as cores
        Assert.Equal(4, sent.Count(p => p.Pkt == OpenRgbProtocol.SetCustomMode));
        var last = sent.Where(p => p.Pkt == OpenRgbProtocol.UpdateLeds).GroupBy(p => p.Dev).Select(g => g.Last()).ToList();
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, last.Select(p => p.Dev).OrderBy(x => x));
        Assert.All(last, p =>
        {
            Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(p.Data.AsSpan(4)));
            Assert.Equal(new byte[] { 0, 128, 0, 0 }, p.Data[6..10]);   // verde a 50 %
        });

        server.Received.Clear();
        ram.SetPower(false);
        await ram.WhenSent();
        Assert.All(server.Received.Where(p => p.Pkt == OpenRgbProtocol.UpdateLeds),
            p => Assert.Equal(new byte[] { 0, 0, 0, 0 }, p.Data[6..10]));
        Assert.DoesNotContain(server.Received, p => p.Pkt == OpenRgbProtocol.SetCustomMode);   // só na 1.ª vez

        // Escolher a gráfica
        hub.SetOpenRgbSelected("AMD RX 6xxx GPU", true);
        var gpu = hub.OpenRgbLights.Single(l => l.GroupName == "AMD RX 6xxx GPU");
        Assert.Equal(new RgbColor(0, 0, 255), gpu.Color);           // (0,0,128) → cor + brilho 128
        Assert.Equal(128, gpu.Brightness);
        Assert.Equal(2, hub.Lights.Count);
    }

    [Fact]
    public async Task Reloads_on_device_list_update_and_reconnects()
    {
        await using var server = new FakeOpenRgbServer();
        await using var client = new OpenRgbClient { RetryDelay = TimeSpan.FromMilliseconds(50) };
        int reloads = 0;
        client.DevicesReloaded += () => Interlocked.Increment(ref reloads);
        await client.RestartAsync("127.0.0.1", server.Port);
        await Until(() => client.State == OpenRgbState.Connected);

        server.Devices.RemoveAt(0);
        await server.PushDeviceListUpdated();
        await Until(() => client.Devices.Count == 5);

        server.DropClient();
        await Until(() => server.Connections >= 2 && client.State == OpenRgbState.Connected);
        Assert.Equal(5, client.Devices.Count);
        Assert.True(reloads >= 3);
    }

    [Fact]
    public async Task Reports_not_connected_when_server_is_absent()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        int free = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        await using var client = new OpenRgbClient { RetryDelay = TimeSpan.FromMilliseconds(50) };
        await client.RestartAsync("127.0.0.1", free);
        await Until(() => client.State == OpenRgbState.Disconnected);
        Assert.Contains("SDK server", client.StateText);
        await Assert.ThrowsAsync<IOException>(() => client.SetColorAsync(0, 1, 1, 2, 3));
    }
}
