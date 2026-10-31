using System;
using System.Linq;
using Pine3LightControl.Core.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Pine3LightControl.Bluetooth;

internal sealed record BleAdvert(string Address, string Name, short Rssi, bool HasFfe0);

/// <summary>Procura dispositivos BLE por anúncios (scan ativo, para obter os nomes).</summary>
internal sealed class BleScanner : IDisposable
{
    private BluetoothLEAdvertisementWatcher? _watcher;

    public event Action<BleAdvert>? Found;
    public event Action<string?>? Stopped;   // null = parado normalmente; senão o erro

    public bool IsRunning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public void Start()
    {
        Stop();
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, a) =>
        {
            var name = a.Advertisement.LocalName ?? "";
            bool ffe0 = a.Advertisement.ServiceUuids.Contains(Sp110e.ServiceUuid);
            Found?.Invoke(new BleAdvert(BleAddress.Format(a.BluetoothAddress), name, a.RawSignalStrengthInDBm, ffe0));
        };
        w.Stopped += (_, a) =>
            Stopped?.Invoke(a.Error == Windows.Devices.Bluetooth.BluetoothError.Success ? null : a.Error.ToString());
        _watcher = w;
        w.Start();
    }

    public void Stop()
    {
        try { _watcher?.Stop(); } catch { }
        _watcher = null;
    }

    public void Dispose() => Stop();
}
