using Pine3LightControl.Core;
using Pine3LightControl.Core.HomeAssistant;
using Pine3LightControl.Core.Lights;
using Xunit;

public class HaClientTests
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

    [Fact]
    public async Task Test_button_reports_counts_and_bad_token()
    {
        await using var ha = new FakeHomeAssistant();
        var ok = await HaClient.TestAsync(ha.Url, FakeHomeAssistant.Token, false);
        Assert.True(ok.Ok, ok.Message);
        Assert.Contains("3 controllable of 4", ok.Message);

        var bad = await HaClient.TestAsync(ha.Url, "nope", false);
        Assert.False(bad.Ok);
        Assert.Contains("Token rejected", bad.Message);

        var noServer = await HaClient.TestAsync("http://127.0.0.1:1", "x", false);
        Assert.False(noServer.Ok);
    }

    [Fact]
    public async Task Loads_states_calls_services_and_follows_events()
    {
        await using var ha = new FakeHomeAssistant();
        await using var c = new HaClient();
        await c.RestartAsync(ha.Url, FakeHomeAssistant.Token, false);
        await Until(() => c.State == HaConnectionState.Connected);
        Assert.Equal(4, c.Entities.Count);

        var desk = new HaLight(c, "light.desk");
        Assert.True(desk.IsAvailable);
        Assert.Equal("Desk lamp", desk.Name);
        Assert.True((desk.Features & LightFeatures.Color) != 0);
        Assert.Equal(2200, desk.MinKelvin);

        // Liga, cor, brilho (agregado)
        desk.SetPower(true);
        desk.SetColor(new RgbColor(255, 0, 0));
        for (int i = 10; i <= 200; i += 10) desk.SetBrightness(i);
        await desk.WhenSent();
        var calls = ha.ServiceCalls.ToArray();
        Assert.True(calls.Length <= 4, $"{calls.Length} chamadas");
        var last = calls.Last();
        Assert.Equal("light", last["domain"]!.GetValue<string>());
        Assert.Equal(200, last["service_data"]!["brightness"]!.GetValue<int>());
        Assert.Contains(calls, m => m["service_data"]!["rgb_color"] is not null);

        // Evento vindo do HA atualiza a luz
        c.EntityUpdated += e => { if (e.EntityId == desk.EntityId) desk.Update(e); };
        await ha.PushState("light.desk", "off");
        await Until(() => !desk.IsOn);

        // Cena
        var scene = new HaLight(c, "scene.movie");
        Assert.Equal(LightFeatures.Action, scene.Features);
        scene.Activate();
        await scene.WhenSent();
        Assert.Contains(ha.ServiceCalls, m => m["domain"]!.GetValue<string>() == "scene" &&
                                              m["service"]!.GetValue<string>() == "turn_on");
    }

    [Fact]
    public async Task Reconnects_after_connection_loss_but_not_after_bad_token()
    {
        await using var ha = new FakeHomeAssistant();
        await using var c = new HaClient { Backoff = new[] { TimeSpan.FromMilliseconds(50) } };
        await c.RestartAsync(ha.Url, FakeHomeAssistant.Token, false);
        await Until(() => c.State == HaConnectionState.Connected);
        ha.DropClient();
        await Until(() => ha.Connections >= 2 && c.State == HaConnectionState.Connected);

        await c.RestartAsync(ha.Url, "wrong", false);
        await Until(() => c.State == HaConnectionState.AuthFailed);
        var n = ha.Connections;
        await Task.Delay(300);
        Assert.Equal(n, ha.Connections);
    }

    [Fact]
    public async Task Hub_tracks_selected_entities()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "p3lc-test-" + Guid.NewGuid());
        await using var ha = new FakeHomeAssistant();
        var cfg = new AppConfig();
        cfg.HomeAssistant.Url = ha.Url;
        cfg.HomeAssistant.Token = FakeHomeAssistant.Token;
        cfg.HomeAssistant.Entities.Add("switch.plug");
        await using var hub = new LightHub(cfg);
        int listChanges = 0;
        hub.ListChanged += () => listChanges++;
        await hub.StartAsync();
        await Until(() => hub.Ha.State == HaConnectionState.Connected);
        await Until(() => hub.Lights.Single().IsAvailable);
        Assert.True(hub.Lights[0].IsOn);

        hub.SetHaEntitySelected("light.desk", true);
        Assert.Equal(new[] { "ha:switch.plug", "ha:light.desk" }, hub.Lights.Select(l => l.Id));
        hub.MoveHaEntity("light.desk", -1);
        Assert.Equal("ha:light.desk", hub.Lights[0].Id);

        await ha.PushState("switch.plug", "off");
        await Until(() => !hub.HaLights.First(l => l.EntityId == "switch.plug").IsOn);

        hub.SetAllPower(true);
        await Until(() => ha.ServiceCalls.Count >= 2);

        hub.SetHaEntitySelected("light.desk", false);
        Assert.Single(hub.Lights);
        Assert.True(listChanges >= 3);

        var saved = AppConfig.Load();
        Assert.Equal(new[] { "switch.plug" }, saved.HomeAssistant.Entities);
    }
}
