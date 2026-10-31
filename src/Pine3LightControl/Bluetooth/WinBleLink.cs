using System;
using System.Collections.Generic;
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
using Windows.Foundation;
using Windows.Security.Cryptography;

namespace Pine3LightControl.Bluetooth;

/// <summary>Ligação GATT com as APIs WinRT (Windows.Devices.Bluetooth).</summary>
internal sealed class WinBleLink : IBleLink
{
    /// <summary>Limite por passo: o Windows pode ficar à espera para sempre de um dispositivo.</summary>
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    private readonly BleDeviceConfig _cfg;
    private readonly Action _saveConfig;
    private readonly ulong _address;
    private BluetoothLEDevice? _dev;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _ch;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithResponse;
    private volatile bool _connected;

    public WinBleLink(BleDeviceConfig cfg, Action saveConfig)
    {
        _cfg = cfg;
        _saveConfig = saveConfig;
        _address = BleAddress.Parse(cfg.Address);
    }

    private string Label => string.IsNullOrWhiteSpace(_cfg.Name) ? _cfg.Address : _cfg.Name;

    public bool IsConnected => _connected;
    public string LastError { get; private set; } = "";
    public event Action<byte[]>? Notified;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? Progress;

    private void Step(string what)
    {
        Log.Write($"[BLE] {Label}: {what}");
        Progress?.Invoke(what);
    }

    /// <summary>Espera por uma operação WinRT com limite de tempo (cancela-a se exceder).</summary>
    internal static async Task<T> Timed<T>(IAsyncOperation<T> op, string what, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StepTimeout);
        try { return await op.AsTask(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer while {what} ({StepTimeout.TotalSeconds:0} s)");
        }
    }

    /// <summary>Tipos de endereço a experimentar: o conhecido primeiro, depois o outro.</summary>
    internal static BluetoothAddressType[] AddressTypes(string known) => known switch
    {
        "Random" => new[] { BluetoothAddressType.Random, BluetoothAddressType.Public },
        _ => new[] { BluetoothAddressType.Public, BluetoothAddressType.Random },
    };

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        await CleanupAsync().ConfigureAwait(false);
        LastError = "";
        var problem = await BleAdapter.ProblemAsync().ConfigureAwait(false);
        if (problem is not null)
        {
            LastError = problem;
            return false;
        }

        var errors = new List<string>();
        foreach (var type in AddressTypes(_cfg.AddressType))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryConnectAsync(type, ct).ConfigureAwait(false))
                {
                    var t = type.ToString();
                    if (_cfg.AddressType != t)
                    {
                        _cfg.AddressType = t;
                        _saveConfig();
                    }
                    Step("connected");
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanupAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception e)
            {
                LastError = e.Message.Split('\n')[0].Trim();
            }
            Log.Write($"[BLE] {Label}: falhou com endereço {type}: {LastError}");
            errors.Add(LastError);
            await CleanupAsync().ConfigureAwait(false);
        }
        LastError = errors.LastOrDefault() ?? "not found";
        return false;
    }

    private async Task<bool> TryConnectAsync(BluetoothAddressType type, CancellationToken ct)
    {
        var kind = type == BluetoothAddressType.Random ? "random" : "public";
        Step($"opening device ({kind} address)");
        var dev = await Timed(BluetoothLEDevice.FromBluetoothAddressAsync(_address, type), "opening the device", ct)
            .ConfigureAwait(false);
        if (dev is null)
        {
            LastError = "device not found";
            return false;
        }
        _dev = dev;

        // Pede ao Windows para manter a ligação aberta (senão fecha-a ao fim de uns segundos sem tráfego).
        try
        {
            _session = await Timed(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId), "opening the GATT session", ct)
                .ConfigureAwait(false);
            _session.MaintainConnection = true;
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Write($"[BLE] {Label}: GattSession: {e.Message}"); }

        Step("reading services");
        var sr = await Timed(dev.GetGattServicesForUuidAsync(Sp110e.ServiceUuid, BluetoothCacheMode.Uncached),
            "reading services", ct).ConfigureAwait(false);
        if (sr.Status != GattCommunicationStatus.Success)
        {
            LastError = Describe(sr.Status);
            return false;
        }
        if (sr.Services.Count == 0)
        {
            LastError = "no FFE0 service: not an SP110E-type controller";
            return false;
        }
        _service = sr.Services[0];
        foreach (var extra in sr.Services.Skip(1)) extra.Dispose();

        Step("reading characteristics");
        var cr = await Timed(_service.GetCharacteristicsForUuidAsync(Sp110e.CharacteristicUuid, BluetoothCacheMode.Uncached),
            "reading characteristics", ct).ConfigureAwait(false);
        if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0)
        {
            LastError = cr.Status == GattCommunicationStatus.Success ? "no FFE1 characteristic" : Describe(cr.Status);
            return false;
        }
        _ch = cr.Characteristics[0];
        var props = _ch.CharacteristicProperties;
        _writeOption = props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        Log.Write($"[BLE] {Label}: FFE1 [{props}]");

        if (props.HasFlag(GattCharacteristicProperties.Notify))
        {
            Step("subscribing to notifications");
            try
            {
                var st = await Timed(_ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify), "subscribing to notifications", ct)
                    .ConfigureAwait(false);
                if (st == GattCommunicationStatus.Success) _ch.ValueChanged += OnValueChanged;
                else Log.Write($"[BLE] {Label}: notificações indisponíveis ({st})");
            }
            catch (TimeoutException e) { Log.Write($"[BLE] {Label}: {e.Message} — continua sem notificações"); }
        }

        dev.ConnectionStatusChanged += OnConnectionStatusChanged;
        _connected = true;
        return true;
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
            Log.Write($"[BLE] {Label}: ligação perdida");
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
    public static async Task<string> DescribeAsync(string address, string addressType)
    {
        var sb = new StringBuilder();
        var problem = await ProblemAsync();
        if (problem is not null) return $"{address}: {problem}";
        foreach (var type in WinBleLink.AddressTypes(addressType))
        {
            sb.AppendLine($"{address} ({type} address)");
            try
            {
                using var dev = await WinBleLink.Timed(BluetoothLEDevice.FromBluetoothAddressAsync(BleAddress.Parse(address), type),
                    "opening the device", CancellationToken.None);
                if (dev is null) { sb.AppendLine("  device not found"); continue; }
                sb.AppendLine($"  name: {dev.Name}  status: {dev.ConnectionStatus}");
                var sr = await WinBleLink.Timed(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached), "reading services",
                    CancellationToken.None);
                if (sr.Status != GattCommunicationStatus.Success) { sb.AppendLine($"  services: {sr.Status}"); continue; }
                foreach (var s in sr.Services)
                {
                    using (s)
                    {
                        sb.AppendLine($"  service {s.Uuid}");
                        var cr = await WinBleLink.Timed(s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached),
                            "reading characteristics", CancellationToken.None);
                        if (cr.Status != GattCommunicationStatus.Success) { sb.AppendLine($"    ({cr.Status})"); continue; }
                        foreach (var c in cr.Characteristics)
                            sb.AppendLine($"    char {c.Uuid}  [{c.CharacteristicProperties}]");
                    }
                }
                return sb.ToString();
            }
            catch (Exception e) { sb.AppendLine($"  {e.Message}"); }
        }
        return sb.ToString();
    }
}
