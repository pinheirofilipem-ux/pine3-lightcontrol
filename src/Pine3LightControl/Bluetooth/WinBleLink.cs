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
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Security.Cryptography;

namespace Pine3LightControl.Bluetooth;

/// <summary>Ligação GATT com as APIs WinRT (Windows.Devices.Bluetooth).</summary>
internal sealed class WinBleLink : IBleLink
{
    /// <summary>Limite por passo: o Windows pode ficar à espera para sempre de um dispositivo.</summary>
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    private readonly BleDeviceConfig _cfg;
    private readonly Action _saveConfig;
    private readonly ulong _address;
    private BluetoothLEDevice? _dev;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _ch;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithResponse;
    private volatile bool _connected;
    private object? _connParams;               // pedido de parâmetros de ligação (Windows 11)
    private CancellationTokenSource? _phaseCts; // cancela a descoberta se a ligação cair
    private DateTime _openedAt;
    private int _attempt;
    private static bool _adapterLogged;
    private GattCharacteristic? _ffe2;

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

        if (!_adapterLogged)
        {
            _adapterLogged = true;
            Log.Write($"[BLE] adaptador: {await BleAdapter.DescribeAdapterAsync().ConfigureAwait(false)}");
        }

        // 1) O controlador está a anunciar? Se não, quase sempre está ligado ao telemóvel.
        Step("looking for the controller");
        var seen = await FindAdvertAsync(ct).ConfigureAwait(false);
        if (seen is null)
        {
            LastError = "not advertising: probably still connected to the phone (close LED Hue or turn off the phone's Bluetooth), or off / out of range";
            Log.Write($"[BLE] {Label}: {LastError}");
            return false;
        }
        var type = seen.Value;
        if (_cfg.AddressType != type.ToString())
        {
            _cfg.AddressType = type.ToString();
            _saveConfig();
        }

        // 2) Ligar com o tipo de endereço anunciado. Alterna entre duas estratégias:
        //    A) pelo endereço, pedindo só o serviço FFE0;
        //    B) como o projeto C# SiKaminski/BluetoothLE-LED-Strip: DeviceWatcher +
        //       FromIdAsync, todos os serviços, e handshake FFE2 <- 01 00, FFE1 <- 01 B7 E3 D5.
        bool viaWatcher = _attempt++ % 2 == 0;
        try
        {
            if (await TryConnectAsync(type, viaWatcher, ct).ConfigureAwait(false))
            {
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
        Log.Write($"[BLE] {Label}: falhou ({(viaWatcher ? "B" : "A")}): {LastError}");
        await CleanupAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Procura o anúncio do controlador (scan ativo, até 8 s). Além de confirmar que está
    /// disponível, ter um scan ativo ajuda o Windows a estabelecer a ligação.
    /// </summary>
    private async Task<BluetoothAddressType?> FindAdvertAsync(CancellationToken ct)
    {
        var found = new TaskCompletionSource<BluetoothAddressType>(TaskCreationOptions.RunContinuationsAsynchronously);
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, a) => { if (a.BluetoothAddress == _address) found.TrySetResult(a.BluetoothAddressType); };
        try
        {
            w.Start();
            return await found.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Sem scan (ex.: outro programa a usar o rádio): tenta ligar com o tipo guardado
            Log.Write($"[BLE] {Label}: scan indisponível: {e.Message}");
            return _cfg.AddressType == "Random" ? BluetoothAddressType.Random : BluetoothAddressType.Public;
        }
        finally
        {
            try { w.Stop(); } catch { }
        }
    }

    /// <summary>Espera que a sessão GATT fique ativa (o dispositivo aceitou a ligação).</summary>
    internal static async Task<bool> WaitActiveAsync(GattSession session, TimeSpan timeout, CancellationToken ct)
    {
        var active = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs> h = (_, e) =>
        {
            if (e.Status == GattSessionStatus.Active) active.TrySetResult(true);
        };
        session.SessionStatusChanged += h;
        try
        {
            if (session.SessionStatus == GattSessionStatus.Active) return true;
            return await active.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        finally { session.SessionStatusChanged -= h; }
    }

    private async Task<bool> TryConnectAsync(BluetoothAddressType type, bool viaWatcher, CancellationToken ct)
    {
        var kind = type == BluetoothAddressType.Random ? "random" : "public";
        BluetoothLEDevice? dev;
        if (viaWatcher)
        {
            Step("finding the device in Windows (B)");
            var id = await FindDeviceIdAsync(ct).ConfigureAwait(false);
            if (id is null)
            {
                LastError = "device not listed by Windows";
                return false;
            }
            Log.Write($"[BLE] {Label}: id {id}");
            Step("opening device (B)");
            dev = await Timed(BluetoothLEDevice.FromIdAsync(id), "opening the device", ct).ConfigureAwait(false);
        }
        else
        {
            Step($"opening device ({kind} address, A)");
            dev = await Timed(BluetoothLEDevice.FromBluetoothAddressAsync(_address, type), "opening the device", ct)
                .ConfigureAwait(false);
        }
        if (dev is null)
        {
            LastError = "device not found";
            return false;
        }
        _dev = dev;
        _openedAt = DateTime.UtcNow;
        dev.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Pede ao Windows para manter a ligação aberta (senão fecha-a ao fim de uns segundos sem tráfego).
        // Na estratégia B só se faz depois da descoberta (como no projeto C# que a inspira).
        if (!viaWatcher)
        try
        {
            _session = await Timed(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId), "opening the GATT session", ct)
                .ConfigureAwait(false);
            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Write($"[BLE] {Label}: GattSession: {e.Message}"); }

        if (_session is not null && !viaWatcher)
        {
            Step("waiting for the controller to accept the connection");
            if (!await WaitActiveAsync(_session, StepTimeout, ct).ConfigureAwait(false))
            {
                LastError = $"the controller didn't accept the connection ({StepTimeout.TotalSeconds:0} s) — is it connected to the phone?";
                return false;
            }
            Log.Write($"[BLE] {Label}: sessão GATT ativa (MTU {_session.MaxPduSize})");
        }
        if (!viaWatcher) RequestFastConnection(dev);

        if (viaWatcher)
        {
            // B) todos os serviços / características, modo por omissão do Windows
            Step("reading all services (B)");
            var t0 = DateTime.UtcNow;
            var all = await Timed(dev.GetGattServicesAsync(), "reading services", ct).ConfigureAwait(false);
            Log.Write($"[BLE] {Label}: serviços (B): {all.Status}, {all.Services.Count} em {(DateTime.UtcNow - t0).TotalSeconds:0.0} s: " +
                      string.Join(", ", all.Services.Select(x => x.Uuid.ToString("D")[4..8])));
            if (all.Status != GattCommunicationStatus.Success)
            {
                LastError = Describe(all.Status);
                return false;
            }
            _service = all.Services.FirstOrDefault(x => x.Uuid == Sp110e.ServiceUuid);
            foreach (var other in all.Services.Where(x => !ReferenceEquals(x, _service))) other.Dispose();
            if (_service is null)
            {
                LastError = "no FFE0 service: not an SP110E-type controller";
                return false;
            }
            Step("reading characteristics (B)");
            var chars = await Timed(_service.GetCharacteristicsAsync(), "reading characteristics", ct).ConfigureAwait(false);
            if (chars.Status != GattCommunicationStatus.Success)
            {
                LastError = Describe(chars.Status);
                return false;
            }
            _ch = chars.Characteristics.FirstOrDefault(c => c.Uuid == Sp110e.CharacteristicUuid);
            _ffe2 = chars.Characteristics.FirstOrDefault(c => c.Uuid == Ffe2Uuid);
            if (_ch is null)
            {
                LastError = "no FFE1 characteristic";
                return false;
            }
        }
        else
        {
            Step("reading services (A)");
            var sr = await DiscoverAsync(m => dev.GetGattServicesForUuidAsync(Sp110e.ServiceUuid, m),
                r => r.Status == GattCommunicationStatus.Success && r.Services.Count > 0, r => $"{r.Status}, {r.Services.Count}",
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
            var service = _service;
            var cr = await DiscoverAsync(m => service.GetCharacteristicsForUuidAsync(Sp110e.CharacteristicUuid, m),
                r => r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0, r => $"{r.Status}, {r.Characteristics.Count}",
                "reading characteristics", ct).ConfigureAwait(false);
            if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0)
            {
                LastError = cr.Status == GattCommunicationStatus.Success ? "no FFE1 characteristic" : Describe(cr.Status);
                return false;
            }
            _ch = cr.Characteristics[0];
            var service2 = _service;
            try
            {
                var c2 = await Timed(service2.GetCharacteristicsForUuidAsync(Ffe2Uuid, BluetoothCacheMode.Cached), "reading FFE2", ct)
                    .ConfigureAwait(false);
                _ffe2 = c2.Characteristics.FirstOrDefault();
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Write($"[BLE] {Label}: FFE2: {e.Message}"); }
        }

        await HandshakeAsync().ConfigureAwait(false);

        if (viaWatcher && _session is null)
        {
            try
            {
                _session = await Timed(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId), "opening the GATT session", ct)
                    .ConfigureAwait(false);
                _session.MaintainConnection = true;
                _session.SessionStatusChanged += OnSessionStatusChanged;
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Write($"[BLE] {Label}: GattSession: {e.Message}"); }
        }
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

        _connected = true;
        Log.Write($"[BLE] {Label}: pronto em {(DateTime.UtcNow - _openedAt).TotalSeconds:0.0} s");
        return true;
    }

    private static readonly Guid Ffe2Uuid = new("0000ffe2-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// Handshake visto em SiKaminski/BluetoothLE-LED-Strip e no gist do RufusVS:
    /// FFE2 &lt;- 01 00 e FFE1 &lt;- 01 B7 E3 D5 logo após ligar (alguns firmwares
    /// cortam a ligação sem isto). Falhas aqui não impedem a ligação.
    /// </summary>
    private async Task HandshakeAsync()
    {
        try
        {
            if (_ffe2 is not null)
            {
                var r = await _ffe2.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(new byte[] { 0x01, 0x00 }))
                    .AsTask().ConfigureAwait(false);
                Log.Write($"[BLE] {Label}: handshake FFE2: {r.Status}");
            }
            if (_ch is not null)
            {
                var opt = _ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                    ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
                var r = await _ch.WriteValueWithResultAsync(
                    CryptographicBuffer.CreateFromByteArray(new byte[] { 0x01, 0xB7, 0xE3, 0xD5 }), opt).AsTask().ConfigureAwait(false);
                Log.Write($"[BLE] {Label}: handshake FFE1: {r.Status}");
            }
        }
        catch (Exception e) { Log.Write($"[BLE] {Label}: handshake: {e.Message}"); }
    }

    /// <summary>Encontra o dispositivo como o Windows o lista (AssociationEndpoint), pelo endereço.</summary>
    private async Task<string?> FindDeviceIdAsync(CancellationToken ct)
    {
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aqs = $"({BluetoothLEDevice.GetDeviceSelectorFromPairingState(false)}) OR ({BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)})";
        var w = DeviceInformation.CreateWatcher(aqs,
            new[] { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" },
            DeviceInformationKind.AssociationEndpoint);
        w.Added += (_, d) =>
        {
            if (d.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a) &&
                string.Equals(a as string, _cfg.Address, StringComparison.OrdinalIgnoreCase))
                found.TrySetResult(d.Id);
        };
        w.Updated += (_, _) => { };   // sem estes handlers o DeviceWatcher não entrega eventos
        w.Removed += (_, _) => { };
        try
        {
            w.Start();
            return await found.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            try
            {
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
            }
            catch { }
        }
    }

    /// <summary>
    /// Descoberta GATT: primeiro a cache do Windows (instantâneo depois da 1.ª ligação),
    /// depois o dispositivo. Aborta logo se a ligação cair a meio (o SP110E corta
    /// ligações em que não recebe nada) em vez de ficar à espera.
    /// </summary>
    private async Task<T> DiscoverAsync<T>(Func<BluetoothCacheMode, IAsyncOperation<T>> op, Func<T, bool> ok,
        Func<T, string> describe, string what, CancellationToken ct)
    {
        T last = default!;
        foreach (var mode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StepTimeout);
            _phaseCts = cts;
            var t0 = DateTime.UtcNow;
            try
            {
                last = await op(mode).AsTask(cts.Token).ConfigureAwait(false);
                Log.Write($"[BLE] {Label}: {what} ({mode}): {describe(last)} em {(DateTime.UtcNow - t0).TotalSeconds:0.0} s");
                if (ok(last)) return last;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                bool dropped = _dev?.ConnectionStatus == BluetoothConnectionStatus.Disconnected;
                throw new TimeoutException(dropped
                    ? $"the controller dropped the connection while {what}"
                    : $"no answer while {what} ({StepTimeout.TotalSeconds:0} s)");
            }
            finally { _phaseCts = null; }
        }
        return last;
    }

    /// <summary>Pede intervalos de ligação curtos (Windows 11): a descoberta acaba antes de o controlador desistir.</summary>
    private void RequestFastConnection(BluetoothLEDevice dev)
    {
        try
        {
            if (!ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "RequestPreferredConnectionParameters"))
                return;
            var req = dev.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            _connParams = req;
            Log.Write($"[BLE] {Label}: parâmetros de ligação rápidos: {req.Status}");
        }
        catch (Exception e) { Log.Write($"[BLE] {Label}: parâmetros de ligação: {e.Message}"); }
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args) =>
        Log.Write($"[BLE] {Label}: sessão {args.Status}{(args.Error != BluetoothError.Success ? $" ({args.Error})" : "")} " +
                  $"+{(DateTime.UtcNow - _openedAt).TotalSeconds:0.0} s");

    private static string Describe(GattCommunicationStatus s) => s switch
    {
        GattCommunicationStatus.Unreachable => "unreachable: out of range, off, or connected to the phone app",
        GattCommunicationStatus.AccessDenied => "access denied",
        GattCommunicationStatus.ProtocolError => "protocol error",
        _ => s.ToString(),
    };

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Log.Write($"[BLE] {Label}: ligação {sender.ConnectionStatus} +{(DateTime.UtcNow - _openedAt).TotalSeconds:0.0} s");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_connected)
            try { _phaseCts?.Cancel(); } catch { }
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
        _ffe2 = null;
        try { if (_dev is not null) _dev.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
        try { if (_session is not null) _session.SessionStatusChanged -= OnSessionStatusChanged; } catch { }
        try { (_connParams as IDisposable)?.Dispose(); } catch { }
        _connParams = null;
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

    /// <summary>Nome e endereço do adaptador Bluetooth que o Windows está a usar.</summary>
    public static async Task<string> DescribeAdapterAsync()
    {
        try
        {
            var a = await BluetoothAdapter.GetDefaultAsync();
            if (a is null) return "none";
            var di = await DeviceInformation.CreateFromIdAsync(a.DeviceId);
            return $"{di.Name} [{BleAddress.Format(a.BluetoothAddress)}] LE={a.IsLowEnergySupported} central={a.IsCentralRoleSupported}";
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
                using var session = await WinBleLink.Timed(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId),
                    "opening the GATT session", CancellationToken.None);
                session.MaintainConnection = true;
                var active = await WinBleLink.WaitActiveAsync(session, WinBleLink.StepTimeout, CancellationToken.None);
                sb.AppendLine($"  GATT session: {session.SessionStatus}{(active ? $" (MTU {session.MaxPduSize})" : " — the device didn't accept the connection")}");
                if (!active) continue;
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
