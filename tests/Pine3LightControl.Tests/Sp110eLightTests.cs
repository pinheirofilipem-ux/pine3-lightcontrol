using Pine3LightControl.Core;
using Pine3LightControl.Core.Bluetooth;
using Pine3LightControl.Core.Lights;
using Xunit;

/// <summary>Ligação BLE falsa: regista as escritas e responde a GET_INFO.</summary>
sealed class FakeLink : IBleLink
{
    public readonly List<byte[]> Writes = new();
    public bool FailConnect;
    public int ConnectCalls;
    public byte[] InfoReply = { 0, 5, 100, 128, 3, 2, 0, 60, 1, 2, 3, 0 };

    public bool IsConnected { get; private set; }
    public string LastError => FailConnect ? "fake failure" : "";
    public event Action<byte[]>? Notified;
    public event Action<bool>? ConnectionChanged;

    public Task<bool> ConnectAsync(CancellationToken ct)
    {
        ConnectCalls++;
        IsConnected = !FailConnect;
        if (IsConnected) ConnectionChanged?.Invoke(true);
        return Task.FromResult(IsConnected);
    }

    public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }

    public Task WriteAsync(byte[] data)
    {
        lock (Writes) Writes.Add(data);
        if (data[3] == Sp110e.CmdGetInfo || data[3] == Sp110e.CmdCheckDevice) Notified?.Invoke(InfoReply);
        return Task.CompletedTask;
    }

    public void Drop() { IsConnected = false; ConnectionChanged?.Invoke(false); }
    public byte[] LastCommand(byte cmd) { lock (Writes) return Writes.Last(w => w[3] == cmd); }
    public int Count(byte cmd) { lock (Writes) return Writes.Count(w => w[3] == cmd); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class Sp110eLightTests
{
    private static async Task Until(Func<bool> cond)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Connects_handshakes_and_reads_state()
    {
        var link = new FakeLink();
        await using var light = new Sp110eLight(new BleDeviceConfig { Address = "AA:BB:CC:DD:EE:FF", Name = "Desk" },
            link, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        light.Start();
        await Until(() => light.HasDeviceInfo);
        Assert.True(light.IsAvailable);
        Assert.Equal(1, link.Count(Sp110e.CmdCheckDevice));
        Assert.False(light.IsOn);
        Assert.Equal(5, light.Effect);
        Assert.Equal(128, light.Brightness);
        Assert.Equal(3, light.IcModel);
        Assert.Equal(60, light.PixelCount);
        Assert.Equal(new RgbColor(1, 2, 3), light.Color);
        Assert.Equal("ble:AA:BB:CC:DD:EE:FF", light.Id);
    }

    [Fact]
    public async Task Commands_are_sent_and_coalesced()
    {
        var link = new FakeLink();
        await using var light = new Sp110eLight(new BleDeviceConfig { Address = "X" }, link,
            TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(50));
        light.Start();
        await Until(() => light.HasDeviceInfo);
        await light.WhenSent();   // a resposta fixa do GET_INFO não pode chegar depois

        light.SetPower(true);
        for (int i = 0; i <= 255; i += 5) light.SetBrightness(i);
        light.SetColor(new RgbColor(255, 0, 128));
        light.SetEffect(Sp110e.ModeAuto);
        await light.WhenSent();

        Assert.Equal(new byte[] { 0, 0, 0, 0xAA }, link.LastCommand(Sp110e.CmdOn));
        Assert.Equal(255, link.LastCommand(Sp110e.CmdBrightness)[0]);
        Assert.True(link.Count(Sp110e.CmdBrightness) < 10, "o slider devia ter sido agregado");
        Assert.Equal(new byte[] { 255, 0, 128, 0x1E }, link.LastCommand(Sp110e.CmdColor));
        Assert.Equal(1, link.Count(Sp110e.CmdModeAuto));
        Assert.True(light.IsOn);
    }

    [Fact]
    public async Task Reconnects_after_drop_and_ignores_commands_while_offline()
    {
        var link = new FakeLink { FailConnect = true };
        await using var light = new Sp110eLight(new BleDeviceConfig { Address = "X", Name = "Strip" }, link,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(30));
        light.Start();
        await Until(() => link.ConnectCalls >= 2);
        Assert.False(light.IsAvailable);
        Assert.Contains("fake failure", light.StatusText);

        light.SetPower(true);           // offline: estado local muda, nada é enviado
        Assert.Equal(0, link.Count(Sp110e.CmdOn));

        link.FailConnect = false;
        await Until(() => light.IsAvailable);
        link.Drop();
        Assert.False(light.IsAvailable);
        await Until(() => light.IsAvailable);
        Assert.Equal("Connected", light.StatusText);
    }

    [Fact]
    public async Task Strip_settings_are_written()
    {
        var link = new FakeLink();
        await using var light = new Sp110eLight(new BleDeviceConfig { Address = "X" }, link, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        light.Start();
        await Until(() => light.HasDeviceInfo);
        light.ApplyStripSettings(5, 2, 2000);
        await light.WhenSent();
        Assert.Equal(new byte[] { 5, 0, 0, 0x1C }, link.LastCommand(Sp110e.CmdIcModel));
        Assert.Equal(new byte[] { 2, 0, 0, 0x3C }, link.LastCommand(Sp110e.CmdRgbOrder));
        Assert.Equal(new byte[] { 4, 0, 0, 0x2D }, link.LastCommand(Sp110e.CmdPixelCount)); // limitado a 1024
    }
}
