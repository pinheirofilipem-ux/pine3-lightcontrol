using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pine3LightControl.Core.HomeAssistant;

public enum HaConnectionState { NotConfigured, Connecting, Connected, AuthFailed, Disconnected }

/// <summary>
/// Cliente da API WebSocket do Home Assistant: autentica com um long-lived token,
/// carrega todos os estados, subscreve state_changed e chama serviços.
/// Volta a ligar sozinho (exceto se o token for rejeitado).
/// </summary>
public sealed class HaClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HaEntity> _entities = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _nextId;

    public HaConnectionState State { get; private set; } = HaConnectionState.NotConfigured;
    public string StateText { get; private set; } = "Not configured";
    public string? HaVersion { get; private set; }

    public IReadOnlyDictionary<string, HaEntity> Entities => _entities;

    public event Action? ConnectionChanged;
    /// <summary>Lista completa recarregada (ligação nova).</summary>
    public event Action? EntitiesReloaded;
    public event Action<HaEntity>? EntityUpdated;

    /// <summary>Tempos de espera entre tentativas (os testes encurtam).</summary>
    internal TimeSpan[] Backoff { get; set; } =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    // ── Ciclo de vida ────────────────────────────────────────────────────────

    public async Task RestartAsync(string url, string token, bool allowInvalidCert)
    {
        await StopAsync().ConfigureAwait(false);
        var uri = HaUrl.ToWebSocket(url);
        if (uri is null || string.IsNullOrWhiteSpace(token))
        {
            SetState(HaConnectionState.NotConfigured, "Not configured");
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(uri, token.Trim(), allowInvalidCert, ct));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null) return;
        cts.Cancel();
        try { _ws?.Abort(); } catch { }
        try { if (loop is not null) await loop.ConfigureAwait(false); } catch { }
        cts.Dispose();
        SetState(HaConnectionState.Disconnected, "Disconnected");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void SetState(HaConnectionState s, string text)
    {
        State = s;
        StateText = text;
        try { ConnectionChanged?.Invoke(); } catch (Exception e) { Log.Write($"[HA] {e.Message}"); }
    }

    private async Task LoopAsync(Uri uri, string token, bool allowInvalidCert, CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(HaConnectionState.Connecting, $"Connecting to {uri.Host}…");
            try
            {
                using var ws = CreateSocket(allowInvalidCert);
                _ws = ws;
                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                var auth = await AuthenticateAsync(ws, token, ct).ConfigureAwait(false);
                if (auth is not null)
                {
                    Log.Write($"[HA] autenticação recusada: {auth}");
                    SetState(HaConnectionState.AuthFailed, $"Token rejected: {auth}");
                    return;   // não insistir com um token errado
                }
                attempt = 0;

                // A partir daqui as mensagens chegam pelo ciclo de leitura.
                var reader = Task.Run(() => ReadLoopAsync(ws, ct), ct);
                var states = await RequestAsync(new JsonObject { ["type"] = "get_states" }, ct).ConfigureAwait(false);
                _entities.Clear();
                foreach (var e in states.EnumerateArray())
                    if (HaEntity.FromJson(e) is { } ent) _entities[ent.EntityId] = ent;
                await RequestAsync(new JsonObject { ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, ct)
                    .ConfigureAwait(false);

                Log.Write($"[HA] ligado a {uri.Host} (HA {HaVersion}), {_entities.Count} entidades");
                SetState(HaConnectionState.Connected, $"Connected — Home Assistant {HaVersion}");
                try { EntitiesReloaded?.Invoke(); } catch (Exception e) { Log.Write($"[HA] {e.Message}"); }

                await reader.ConfigureAwait(false);
                if (!ct.IsCancellationRequested) Log.Write("[HA] ligação fechada");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                Log.Write($"[HA] {e.GetType().Name}: {e.Message}");
                SetState(HaConnectionState.Disconnected, $"Disconnected ({Short(e)}) — retrying");
            }
            finally
            {
                _ws = null;
                FailPending();
            }

            if (ct.IsCancellationRequested) break;
            var wait = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            if (State == HaConnectionState.Connected)
                SetState(HaConnectionState.Disconnected, "Connection lost — retrying");
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private static string Short(Exception e) =>
        (e.InnerException?.Message ?? e.Message).Split('\n')[0].Trim();

    private static ClientWebSocket CreateSocket(bool allowInvalidCert)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (allowInvalidCert)
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return ws;
    }

    /// <summary>Devolve null se autenticou, ou a mensagem de erro.</summary>
    private async Task<string?> AuthenticateAsync(ClientWebSocket ws, string token, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var first = await ReceiveAsync(ws, timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("Connection closed before auth");
        if (Type(first) != "auth_required")
            throw new IOException("Not a Home Assistant WebSocket endpoint");
        HaVersion = first.TryGetProperty("ha_version", out var v) ? v.GetString() : null;

        await SendRawAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = token }, timeout.Token)
            .ConfigureAwait(false);
        var reply = await ReceiveAsync(ws, timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("Connection closed during auth");
        return Type(reply) switch
        {
            "auth_ok" => null,
            "auth_invalid" => reply.TryGetProperty("message", out var m) ? m.GetString() ?? "invalid" : "invalid",
            var t => throw new IOException($"Unexpected reply: {t}"),
        };
    }

    private static string? Type(JsonElement e) =>
        e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    // ── Leitura ──────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (msg is null) return;
            // O HA pode juntar várias mensagens num array (coalesce_messages)
            if (msg.Value.ValueKind == JsonValueKind.Array)
                foreach (var m in msg.Value.EnumerateArray()) Dispatch(m);
            else Dispatch(msg.Value);
        }
    }

    private void Dispatch(JsonElement m)
    {
        switch (Type(m))
        {
            case "result":
                if (m.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && _pending.TryRemove(id, out var tcs))
                {
                    if (m.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                        tcs.TrySetResult(m.TryGetProperty("result", out var r) ? r.Clone() : default);
                    else
                    {
                        var err = m.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                            ? em.GetString() : "request failed";
                        tcs.TrySetException(new HaException(err ?? "request failed"));
                    }
                }
                break;
            case "event":
                if (m.TryGetProperty("event", out var ev) && ev.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("entity_id", out var eid) && eid.ValueKind == JsonValueKind.String)
                {
                    var entityId = eid.GetString()!;
                    if (data.TryGetProperty("new_state", out var ns) && HaEntity.FromJson(ns) is { } ent)
                    {
                        _entities[entityId] = ent;
                        try { EntityUpdated?.Invoke(ent); } catch (Exception ex) { Log.Write($"[HA] {ex.Message}"); }
                    }
                    else _entities.TryRemove(entityId, out _);   // entidade removida
                }
                break;
        }
    }

    private static async Task<JsonElement?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    // ── Escrita ──────────────────────────────────────────────────────────────

    private async Task SendRawAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task<JsonElement> RequestAsync(JsonObject msg, CancellationToken ct)
    {
        var ws = _ws ?? throw new HaException("Not connected to Home Assistant");
        var id = Interlocked.Increment(ref _nextId);
        msg["id"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendRawAsync(ws, msg, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void FailPending()
    {
        foreach (var k in _pending.Keys)
            if (_pending.TryRemove(k, out var t)) t.TrySetException(new HaException("Connection lost"));
    }

    /// <summary>Chama um serviço (ex.: light.turn_on) para uma entidade.</summary>
    public Task CallServiceAsync(string domain, string service, string entityId, JsonObject? data = null,
        CancellationToken ct = default)
    {
        if (State != HaConnectionState.Connected) throw new HaException("Not connected to Home Assistant");
        var msg = new JsonObject
        {
            ["type"] = "call_service",
            ["domain"] = domain,
            ["service"] = service,
            ["service_data"] = data ?? new JsonObject(),
            ["target"] = new JsonObject { ["entity_id"] = entityId },
        };
        return RequestAsync(msg, ct);
    }

    /// <summary>Teste isolado (botão "Test"): liga, autentica, conta as entidades e fecha.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(string url, string token, bool allowInvalidCert,
        CancellationToken ct = default)
    {
        var uri = HaUrl.ToWebSocket(url);
        if (uri is null) return (false, "Invalid address");
        if (string.IsNullOrWhiteSpace(token)) return (false, "Missing access token");
        var c = new HaClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var ws = CreateSocket(allowInvalidCert);
            c._ws = ws;
            await ws.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            var err = await c.AuthenticateAsync(ws, token.Trim(), timeout.Token).ConfigureAwait(false);
            if (err is not null) return (false, $"Token rejected: {err}");
            var reader = Task.Run(() => c.ReadLoopAsync(ws, timeout.Token));
            var states = await c.RequestAsync(new JsonObject { ["type"] = "get_states" }, timeout.Token).ConfigureAwait(false);
            int all = 0, usable = 0;
            foreach (var e in states.EnumerateArray())
            {
                all++;
                if (HaEntity.FromJson(e)?.IsSupported == true) usable++;
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }
            return (true, $"OK — Home Assistant {c.HaVersion}, {usable} controllable of {all} entities");
        }
        catch (Exception e) { return (false, Short(e)); }
    }
}

public sealed class HaException(string message) : Exception(message);
