using Pine3LightControl.Core.Lights;

namespace Pine3LightControl.Core.Bluetooth;

/// <summary>
/// Controlador SP110E / SP105E. Mantém a ligação (volta a ligar sozinho),
/// envia comandos por uma <see cref="SendQueue"/> e atualiza o estado a partir
/// das notificações do controlador.
/// </summary>
public sealed class Sp110eLight : LightBase, IAsyncDisposable
{
    private readonly BleDeviceConfig _cfg;
    private readonly IBleLink _link;
    private readonly SendQueue _queue;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _status = "Not connected";
    private bool _connecting;
    private readonly TimeSpan _retry;

    public Sp110eLight(BleDeviceConfig cfg, IBleLink link, TimeSpan? sendGap = null, TimeSpan? retry = null)
    {
        _cfg = cfg;
        _link = link;
        _retry = retry ?? TimeSpan.FromSeconds(10);
        _queue = new SendQueue(sendGap ?? TimeSpan.FromMilliseconds(30));
        _queue.Error += e => Log.Write($"[BLE] {_cfg.Name}: falha a escrever: {e.Message}");
        _link.Notified += OnNotified;
        _link.ConnectionChanged += OnConnectionChanged;
        Effect = Sp110e.ModeStatic;
    }

    public BleDeviceConfig Config => _cfg;
    public override string Id => "ble:" + _cfg.Address;
    public override string Name => string.IsNullOrWhiteSpace(_cfg.Name) ? _cfg.Address : _cfg.Name;
    public override LightSource Source => LightSource.Bluetooth;
    public override bool IsAvailable => _link.IsConnected;
    public override string StatusText => _status;
    public override IReadOnlyList<string> Effects => Sp110e.ModeNames;

    public override LightFeatures Features =>
        LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color |
        LightFeatures.Effects | LightFeatures.Speed |
        (Sp110e.IsRgbw(IcModel) ? LightFeatures.White : 0);

    // Definições do controlador (lidas do próprio dispositivo)
    public int IcModel { get; private set; } = -1;
    public int RgbOrder { get; private set; } = -1;
    public int PixelCount { get; private set; }
    public bool HasDeviceInfo { get; private set; }

    // ── Ligação ──────────────────────────────────────────────────────────────

    /// <summary>Arranca o ciclo de ligação (liga e volta a ligar se cair).</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null) return;
        cts.Cancel();
        try { if (loop is not null) await loop.ConfigureAwait(false); } catch { }
        cts.Dispose();
        _queue.Clear();
        try { await _link.DisconnectAsync().ConfigureAwait(false); } catch { }
        SetStatus("Disconnected");
    }

    public bool IsRunning => _loop is not null;

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_link.IsConnected && !_connecting)
            {
                _connecting = true;
                SetStatus("Connecting…");
                bool ok = false;
                try { ok = await _link.ConnectAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { Log.Write($"[BLE] {Name}: {e.Message}"); }
                finally { _connecting = false; }

                if (ok)
                {
                    Log.Write($"[BLE] {Name} ligado");
                    SetStatus("Connected");
                    // Handshake + pedir o estado
                    _queue.Post("check", () => _link.WriteAsync(Sp110e.CheckDevice()));
                    _queue.Post("info", () => _link.WriteAsync(Sp110e.GetInfo()));
                }
                else
                {
                    var err = string.IsNullOrEmpty(_link.LastError) ? "not found" : _link.LastError;
                    SetStatus($"Not connected ({err}) — retrying");
                }
            }
            try { await Task.Delay(_retry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected)
        {
            _queue.Clear();
            if (_loop is not null) SetStatus("Connection lost — retrying");
        }
        else SetStatus("Connected");
    }

    private void SetStatus(string s)
    {
        _status = s;
        RaiseChanged();
    }

    private void OnNotified(byte[] data)
    {
        var st = Sp110e.ParseStatus(data);
        if (st is null) return;
        IsOn = st.IsOn;
        Effect = st.Mode <= Sp110e.ModeStatic ? st.Mode : -1;
        Speed = st.Speed;
        Brightness = st.Brightness;
        IcModel = st.IcModel;
        RgbOrder = st.RgbOrder;
        PixelCount = st.PixelCount;
        Color = st.Color;
        White = st.White;
        HasDeviceInfo = true;
        RaiseChanged();
    }

    // ── Comandos ─────────────────────────────────────────────────────────────

    private void Send(string key, byte[] cmd)
    {
        if (!_link.IsConnected) return;
        _queue.Post(key, () => _link.WriteAsync(cmd));
    }

    public override void SetPower(bool on)
    {
        IsOn = on;
        Send("power", on ? Sp110e.PowerOn() : Sp110e.PowerOff());
        RaiseChanged();
    }

    public override void SetBrightness(int value)
    {
        Brightness = Math.Clamp(value, 0, 255);
        Send("brightness", Sp110e.Brightness(Brightness));
        RaiseChanged();
    }

    public override void SetColor(RgbColor color)
    {
        Color = color;
        Effect = Sp110e.ModeStatic;
        Send("color", Sp110e.Color(color));
        RaiseChanged();
    }

    public override void SetEffect(int index)
    {
        if (index < 0 || index > Sp110e.ModeStatic) return;
        Effect = index;
        // "Cor fixa" não tem comando próprio: volta-se a ela enviando a cor.
        Send("mode", index == Sp110e.ModeStatic ? Sp110e.Color(Color) : Sp110e.Mode(index));
        RaiseChanged();
    }

    public override void SetSpeed(int value)
    {
        Speed = Math.Clamp(value, 0, 255);
        Send("speed", Sp110e.Speed(Speed));
        RaiseChanged();
    }

    public override void SetWhite(int value)
    {
        White = Math.Clamp(value, 0, 255);
        Send("white", Sp110e.White(White));
        RaiseChanged();
    }

    /// <summary>Grava o tipo de fita no controlador e volta a ler o estado.</summary>
    public void ApplyStripSettings(int icModel, int rgbOrder, int pixels)
    {
        if (icModel >= 0) Send("ic", Sp110e.IcModel(icModel));
        if (rgbOrder >= 0) Send("order", Sp110e.RgbOrder(rgbOrder));
        if (pixels > 0) Send("pixels", Sp110e.PixelCount(Math.Min(pixels, Sp110e.MaxPixels)));
        Send("info", Sp110e.GetInfo());
    }

    public void Rename(string name) => Send("rename", Sp110e.Rename(name));

    public void RequestInfo() => Send("info", Sp110e.GetInfo());

    public Task WhenSent() => _queue.WhenIdle();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _link.Notified -= OnNotified;
        _link.ConnectionChanged -= OnConnectionChanged;
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}
