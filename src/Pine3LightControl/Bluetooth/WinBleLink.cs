using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pine3LightControl.Core;
using Pine3LightControl.Core.Bluetooth;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Security.Cryptography;

namespace Pine3LightControl.Bluetooth;

/// <summary>Ligação GATT com as APIs WinRT (Windows.Devices.Bluetooth).</summary>
internal sealed class WinBleLink : IBleLink
{
    private readonly ulong _address;
    private readonly string _label;
    private BluetoothLEDevice? _dev;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _ch;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithResponse;
    private volatile bool _connected;

    public WinBleLink(string address, string label)
    {
        _address = BleAddress.Parse(address);
        _label = label;
    }

    public bool IsConnected => _connected;
    public string LastError { get; private set; } = "";
    public event Action<byte[]>? Notified;
    public event Action<bool>? ConnectionChanged;

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        await CleanupAsync().ConfigureAwait(false);
        LastError = "";
        try
        {
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask(ct).ConfigureAwait(false);
            if (dev is null)
            {
                LastError = await BleAdapter.ProblemAsync().ConfigureAwait(false) ?? "device not found";
                return false;
            }
            _dev = dev;

            // Pede ao Windows para manter a ligação aberta (senão fecha-a ao fim de uns segundos sem tráfego).
            try
            {
                _session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
                _session.MaintainConnection = true;
            }
            catch (Exception e) { Log.Write($"[BLE] {_label}: GattSession: {e.Message}"); }

            var sr = await dev.GetGattServicesForUuidAsync(Sp110e.ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(ct).ConfigureAwait(false);
            if (sr.Status != GattCommunicationStatus.Success)
            {
                LastError = Describe(sr.Status);
                await CleanupAsync().ConfigureAwait(false);
                return false;
            }
            if (sr.Services.Count == 0)
            {
                LastError = "no FFE0 service: not an SP110E-type controller";
                await CleanupAsync().ConfigureAwait(false);
                return false;
            }
            _service = sr.Services[0];
            foreach (var extra in sr.Services.Skip(1)) extra.Dispose();

            var cr = await _service.GetCharacteristicsForUuidAsync(Sp110e.CharacteristicUuid, BluetoothCacheMode.Uncached)
                .AsTask(ct).ConfigureAwait(false);
            if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0)
            {
                LastError = cr.Status == GattCommunicationStatus.Success ? "no FFE1 characteristic" : Describe(cr.Status);
                await CleanupAsync().ConfigureAwait(false);
                return false;
            }
            _ch = cr.Characteristics[0];
            var props = _ch.CharacteristicProperties;
            _writeOption = props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;

            if (props.HasFlag(GattCharacteristicProperties.Notify))
            {
                var st = await _ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct).ConfigureAwait(false);
                if (st == GattCommunicationStatus.Success) _ch.ValueChanged += OnValueChanged;
                else Log.Write($"[BLE] {_label}: notificações indisponíveis ({st})");
            }

            dev.ConnectionStatusChanged += OnConnectionStatusChanged;
            _connected = true;
            return true;
        }
        catch (OperationCanceledException) { await CleanupAsync().ConfigureAwait(false); throw; }
        catch (Exception e)
        {
            LastError = e.Message.Split('\n')[0].Trim();
            await CleanupAsync().ConfigureAwait(false);
            return false;
        }
    }

    private static string Describe(GattCommunicationStatus s) => s switch
    {
        GattCommunicationStatus.Unreachable => "unreachable: out of range, off, or connected to the phone app",
        GattCommunicationStatus.AccessDenied => "access denied",
        GattCommunicationStatus.ProtocolError => "protocol error",
        _ => s.ToString(),
    };

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _connected)
        {
            Log.Write($"[BLE] {_label}: ligação perdida");
            _connected = false;
            LastError = "connection lost";
            ConnectionChanged?.Invoke(false);
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
        if (data is { Length: > 0 }) Notified?.Invoke(data);
    }

    public async Task WriteAsync(byte[] data)
    {
        var ch = _ch;
        if (ch is null || !_connected) throw new IOException("not connected");
        var r = await ch.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(data), _writeOption)
            .AsTask().ConfigureAwait(false);
        if (r.Status != GattCommunicationStatus.Success)
            throw new IOException($"write failed: {Describe(r.Status)}");
    }

    public Task DisconnectAsync()
    {
        bool was = _connected;
        _ = CleanupAsync();
        if (was) ConnectionChanged?.Invoke(false);
        return Task.CompletedTask;
    }

    private Task CleanupAsync()
    {
        _connected = false;
        try { if (_ch is not null) _ch.ValueChanged -= OnValueChanged; } catch { }
        try { if (_dev is not null) _dev.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
        try { _service?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        try { _dev?.Dispose(); } catch { }
        _ch = null;
        _service = null;
        _session = null;
        _dev = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CleanupAsync().ConfigureAwait(false);
}

internal static class BleAddress
{
    public static ulong Parse(string s) =>
        Convert.ToUInt64(s.Replace(":", "").Replace("-", "").Trim(), 16);

    public static string Format(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}

internal static class BleAdapter
{
    /// <summary>Null se o Bluetooth LE está pronto; senão uma explicação.</summary>
    public static async Task<string?> ProblemAsync()
    {
        try
        {
            var a = await BluetoothAdapter.GetDefaultAsync();
            if (a is null) return "no Bluetooth adapter found";
            if (!a.IsLowEnergySupported) return "this Bluetooth adapter does not support Bluetooth LE";
            var radio = await a.GetRadioAsync();
            if (radio is not null && radio.State != RadioState.On) return "Bluetooth is turned off";
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Lista serviços e características de um dispositivo (para diagnóstico).</summary>
    public static async Task<string> DescribeAsync(string address)
    {
        var sb = new StringBuilder();
        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(BleAddress.Parse(address));
        if (dev is null) return $"{address}: device not found ({await ProblemAsync() ?? "out of range?"})";
        sb.AppendLine($"{dev.Name} [{address}]");
        var sr = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (sr.Status != GattCommunicationStatus.Success) return sb.Append($"  services: {sr.Status}").ToString();
        foreach (var s in sr.Services)
        {
            using (s)
            {
                sb.AppendLine($"  service {s.Uuid}");
                var cr = await s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (cr.Status != GattCommunicationStatus.Success) { sb.AppendLine($"    ({cr.Status})"); continue; }
                foreach (var c in cr.Characteristics)
                    sb.AppendLine($"    char {c.Uuid}  [{c.CharacteristicProperties}]");
            }
        }
        return sb.ToString();
    }
}
