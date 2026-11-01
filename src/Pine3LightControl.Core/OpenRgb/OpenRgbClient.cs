using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Pine3LightControl.Core.OpenRgb;

public enum OpenRgbState { Disabled, Connecting, Connected, Disconnected }

/// <summary>
/// Cliente do servidor SDK do OpenRGB: liga, lê os dispositivos, envia cores e
/// volta a ligar sozinho. Um pedido de cada vez (o servidor responde por ordem).
/// </summary>
public sealed class OpenRgbClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _replyGate = new();
    private (uint PktId, TaskCompletionSource<byte[]> Tcs)? _pending;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private uint _version = OpenRgbProtocol.ClientVersion;
    private readonly HashSet<uint> _customMode = new();
    private IReadOnlyList<OpenRgbDevice> _devices = Array.Empty<OpenRgbDevice>();

    public OpenRgbState State { get; private set; } = OpenRgbState.Disabled;
    public string StateText { get; private set; } = "Off";
    public IReadOnlyList<OpenRgbDevice> Devices => _devices;

    public event Action? ConnectionChanged;
    public event Action? DevicesReloaded;

    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async Task RestartAsync(string host, int port)
    {
        await StopAsync().ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(host, port, ct));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null) return;
        cts.Cancel();
        CloseSocket();
        try { if (loop is not null) await loop.ConfigureAwait(false); } catch { }
        cts.Dispose();
        _devices = Array.Empty<OpenRgbDevice>();
        SetState(OpenRgbState.Disabled, "Off");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void SetState(OpenRgbState s, string text)
    {
        State = s;
        StateText = text;
        try { ConnectionChanged?.Invoke(); } catch (Exception e) { Log.Write($"[OpenRGB] {e.Message}"); }
    }

    private void CloseSocket()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        lock (_replyGate)
        {
            _pending?.Tcs.TrySetException(new IOException("Connection closed"));
            _pending = null;
        }
    }

    private async Task LoopAsync(string host, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SetState(OpenRgbState.Connecting, $"Connecting to {host}:{port}…");
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await tcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
                }
                _tcp = tcp;
                _stream = tcp.GetStream();
                _customMode.Clear();
                var reader = Task.Run(() => ReadLoopAsync(_stream, ct), ct);

                // Versão: o servidor responde com a dele; usamos min(nossa, dele).
                // Servidores muito antigos não conhecem o pedido e não respondem (versão 0).
                uint server = 0;
                try
                {
                    var v = await RequestAsync(0, OpenRgbProtocol.RequestProtocolVersion,
                        OpenRgbProtocol.UInt32(OpenRgbProtocol.ClientVersion), ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    server = BinaryPrimitives.ReadUInt32LittleEndian(v);
                }
                catch (TimeoutException) { }
                _version = Math.Min(OpenRgbProtocol.ClientVersion, server);
                await SendAsync(0, OpenRgbProtocol.SetClientName, Encoding.UTF8.GetBytes("Pine3 Light Control\0"), ct)
                    .ConfigureAwait(false);

                await ReloadDevicesAsync(ct).ConfigureAwait(false);
                Log.Write($"[OpenRGB] ligado a {host}:{port} (protocolo {server}), {_devices.Count} dispositivos");
                SetState(OpenRgbState.Connected, $"Connected — {_devices.Count} device(s)");

                await reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                if (State == OpenRgbState.Connected || State == OpenRgbState.Connecting)
                    Log.Write($"[OpenRGB] {e.GetType().Name}: {e.Message}");
            }
            finally { CloseSocket(); }

            if (ct.IsCancellationRequested) break;
            _devices = Array.Empty<OpenRgbDevice>();
            SetState(OpenRgbState.Disconnected,
                "Not connected — is OpenRGB running with the SDK server started? Retrying…");
            try { DevicesReloaded?.Invoke(); } catch { }
            try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReloadDevicesAsync(CancellationToken ct)
    {
        var countData = await RequestAsync(0, OpenRgbProtocol.RequestControllerCount, ReadOnlyMemory<byte>.Empty, ct)
            .ConfigureAwait(false);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(countData);
        var list = new List<OpenRgbDevice>();
        for (uint i = 0; i < count; i++)
        {
            var data = await RequestAsync(i, OpenRgbProtocol.RequestControllerData,
                OpenRgbProtocol.UInt32(_version), ct).ConfigureAwait(false);
            try { list.Add(OpenRgbProtocol.ParseController(i, data, _version)); }
            catch (FormatException e) { Log.Write($"[OpenRGB] dispositivo {i} ilegível: {e.Message}"); }
        }
        _devices = list;
        _customMode.Clear();
        try { DevicesReloaded?.Invoke(); } catch (Exception e) { Log.Write($"[OpenRGB] {e.Message}"); }
    }

    // ── E/S ───────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(NetworkStream s, CancellationToken ct)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        while (!ct.IsCancellationRequested)
        {
            await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
                throw new IOException("Not an OpenRGB server (bad magic)");
            uint pktId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (size > 8 * 1024 * 1024) throw new IOException("Packet too large");
            var payload = new byte[size];
            if (size > 0) await s.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

            if (pktId == OpenRgbProtocol.DeviceListUpdated)
            {
                // Dispositivos mudaram (rescan no OpenRGB): recarrega fora do ciclo de leitura
                _ = Task.Run(async () =>
                {
                    try { await ReloadDevicesAsync(ct).ConfigureAwait(false); }
                    catch (Exception e) { Log.Write($"[OpenRGB] recarregar: {e.Message}"); }
                }, ct);
                continue;
            }
            lock (_replyGate)
            {
                if (_pending is { } p && p.PktId == pktId)
                {
                    p.Tcs.TrySetResult(payload);
                    _pending = null;
                }
            }
        }
    }

    private async Task SendAsync(uint devId, uint pktId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var s = _stream ?? throw new IOException("Not connected to OpenRGB");
        var pkt = OpenRgbProtocol.Packet(devId, pktId, payload.Span);
        await s.WriteAsync(pkt, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> RequestAsync(uint devId, uint pktId, ReadOnlyMemory<byte> payload, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_replyGate) _pending = (pktId, tcs);
            await SendAsync(devId, pktId, payload, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout ?? RequestTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_replyGate) _pending = null;
            _requestLock.Release();
        }
    }

    /// <summary>Põe todos os LEDs de um dispositivo numa cor (passa-o ao modo Direct na 1.ª vez).</summary>
    public async Task SetColorAsync(uint deviceIndex, int ledCount, byte r, byte g, byte b, CancellationToken ct = default)
    {
        if (State != OpenRgbState.Connected) throw new IOException("Not connected to OpenRGB");
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_customMode.Add(deviceIndex))
                await SendAsync(deviceIndex, OpenRgbProtocol.SetCustomMode, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            var colors = Enumerable.Repeat((r, g, b), Math.Max(ledCount, 0)).ToArray();
            await SendAsync(deviceIndex, OpenRgbProtocol.UpdateLeds, OpenRgbProtocol.UpdateLedsPayload(colors), ct)
                .ConfigureAwait(false);
        }
        finally { _requestLock.Release(); }
    }
}
