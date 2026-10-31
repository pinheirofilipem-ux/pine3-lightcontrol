using System.Text.Json;
using Pine3LightControl.Core.HomeAssistant;
using Pine3LightControl.Core.Lights;
using Xunit;

public class HaEntityTests
{
    private static HaEntity E(string json) => HaEntity.FromJson(JsonDocument.Parse(json).RootElement)!;

    [Fact]
    public void Color_light_features_and_values()
    {
        var e = E("""
            {"entity_id":"light.desk","state":"on","attributes":{
              "friendly_name":"Desk","supported_color_modes":["color_temp","hs"],
              "brightness":127.6,"rgb_color":[255,10,0],"effect_list":["rainbow","pulse"],"effect":"pulse"}}
            """);
        Assert.Equal("Desk", e.FriendlyName);
        Assert.Equal("light", e.Domain);
        Assert.Equal(LightFeatures.OnOff | LightFeatures.Brightness | LightFeatures.Color | LightFeatures.ColorTemp | LightFeatures.Effects,
            e.Features);
        Assert.Equal(128, e.Int("brightness"));
        Assert.Equal(new RgbColor(255, 10, 0), e.Rgb);
    }

    [Fact]
    public void Simple_entities()
    {
        Assert.Equal(LightFeatures.OnOff, E("""{"entity_id":"light.x","state":"off","attributes":{"supported_color_modes":["onoff"]}}""").Features);
        Assert.Equal(LightFeatures.OnOff | LightFeatures.Brightness,
            E("""{"entity_id":"light.x","state":"off","attributes":{"supported_color_modes":["brightness"]}}""").Features);
        Assert.Equal(LightFeatures.OnOff, E("""{"entity_id":"switch.plug","state":"on","attributes":{}}""").Features);
        Assert.Equal(LightFeatures.Action, E("""{"entity_id":"scene.movie","state":"x","attributes":{}}""").Features);
        Assert.False(E("""{"entity_id":"sensor.temp","state":"21","attributes":{}}""").IsSupported);
        Assert.Equal("light.nameless", E("""{"entity_id":"light.nameless","state":"unavailable"}""").FriendlyName);
        Assert.False(E("""{"entity_id":"light.nameless","state":"unavailable"}""").IsAvailable);
    }

    [Theory]
    [InlineData("homeassistant.local:8123", "ws://homeassistant.local:8123/api/websocket")]
    [InlineData("http://192.168.1.10:8123/", "ws://192.168.1.10:8123/api/websocket")]
    [InlineData("https://abc.ui.nabu.casa", "wss://abc.ui.nabu.casa/api/websocket")]
    [InlineData("https://ha.example.com:8443/lovelace/0?x=1", "wss://ha.example.com:8443/api/websocket")]
    public void Url_to_websocket(string input, string expected) =>
        Assert.Equal(expected, HaUrl.ToWebSocket(input)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("http://")]
    public void Invalid_urls(string input) => Assert.Null(HaUrl.ToWebSocket(input));

    [Fact]
    public void Rgb_parse_and_kelvin()
    {
        Assert.Equal(new RgbColor(0xFF, 0x80, 0x00), RgbColor.Parse("#ff8000"));
        Assert.False(RgbColor.TryParse("#12", out _));
        var warm = RgbColor.FromKelvin(2700);
        var cold = RgbColor.FromKelvin(6500);
        Assert.True(warm.R >= warm.B && cold.B > warm.B);
    }
}
