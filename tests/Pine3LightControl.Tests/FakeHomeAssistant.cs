using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>Servidor WebSocket que imita a API do Home Assistant (o essencial).</summary>
sealed class FakeHomeAssistant : IAsyncDisposable
{
    public const string Token = "good-token";
    private readonly WebApplication _app;
    private WebSocket? _client;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _subscriptionId;

    public readonly ConcurrentQueue<JsonObject> ServiceCalls = new();
    public readonly Dictionary<string, JsonObject> States = new()
    {
        ["light.desk"] = State("light.desk", "off", """{"friendly_name":"Desk lamp","supported_color_modes":["hs","color_temp"],"brightness":null,"min_color_temp_kelvin":2200,"max_color_temp_kelvin":6500}"""),
        ["switch.plug"] = State("switch.plug", "on", """{"friendly_name":"Plug"}"""),
        ["scene.movie"] = State("scene.movie", "2026-01-01", """{"friendly_name":"Movie"}"""),
        ["sensor.temp"] = State("sensor.temp", "21", """{"friendly_name":"Temp"}"""),
    };

    public string Url { get; }
    public int Connections;

    private static JsonObject State(string id, string state, string attrs) => new()
    {
        ["entity_id"] = id, ["state"] = state, ["attributes"] = JsonNode.Parse(attrs),
    };

    public FakeHomeAssistant()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        _app = b.Build();
        _app.UseWebSockets();
        _app.Map("/api/websocket", Handle);
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First();
    }

    private async Task Handle(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref Connections);
        _client = ws;
        await Send(new JsonObject { ["type"] = "auth_required", ["ha_version"] = "2026.10.0" });
        var auth = await Receive(ws);
        if (auth?["access_token"]?.GetValue<string>() != Token)
        {
            await Send(new JsonObject { ["type"] = "auth_invalid", ["message"] = "Invalid access token or password" });
            return;
        }
        await Send(new JsonObject { ["type"] = "auth_ok", ["ha_version"] = "2026.10.0" });

        while (ws.State == WebSocketState.Open)
        {
            var m = await Receive(ws);
            if (m is null) break;
            var id = m["id"]!.GetValue<int>();
            switch (m["type"]!.GetValue<string>())
            {
                case "get_states":
                    var arr = new JsonArray();
                    lock (States) foreach (var s in States.Values) arr.Add(s.DeepClone());
                    await Send(Result(id, arr));
                    break;
                case "subscribe_events":
                    _subscriptionId = id;
                    await Send(Result(id, null));
                    break;
                case "call_service":
                    ServiceCalls.Enqueue(m);
                    await Send(Result(id, new JsonObject()));
                    // Imita o HA: o estado muda e é publicado um evento
                    var entity = m["target"]!["entity_id"]!.GetValue<string>();
                    var service = m["service"]!.GetValue<string>();
                    if (service is "turn_on" or "turn_off" && entity != "scene.movie")
                        await PushState(entity, service == "turn_on" ? "on" : "off", m["service_data"] as JsonObject);
                    break;
            }
        }
    }

    private static JsonObject Result(int id, JsonNode? result) => new()
    {
        ["id"] = id, ["type"] = "result", ["success"] = true, ["result"] = result,
    };

    public async Task PushState(string entity, string state, JsonObject? attrs = null)
    {
        JsonObject s;
        lock (States)
        {
            s = (JsonObject)States[entity].DeepClone();
            s["state"] = state;
            if (attrs is not null)
                foreach (var (k, v) in attrs) s["attributes"]![k] = v?.DeepClone();
            States[entity] = s;
        }
        await Send(new JsonObject
        {
            ["id"] = _subscriptionId, ["type"] = "event",
            ["event"] = new JsonObject
            {
                ["event_type"] = "state_changed",
                ["data"] = new JsonObject { ["entity_id"] = entity, ["new_state"] = s.DeepClone() },
            },
        });
    }

    /// <summary>Fecha a ligação atual (simula reinício do HA).</summary>
    public void DropClient() => _client?.Abort();

    private async Task Send(JsonNode msg)
    {
        var ws = _client;
        if (ws is null) return;
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, default); }
        finally { _sendLock.Release(); }
    }

    private static async Task<JsonObject?> Receive(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, default);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage) break;
            }
        }
        catch (WebSocketException) { return null; }
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
