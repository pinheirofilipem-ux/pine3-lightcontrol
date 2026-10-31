namespace Pine3LightControl.Core.HomeAssistant;

public static class HaUrl
{
    /// <summary>
    /// "homeassistant.local:8123", "http://ha:8123/" ou "https://x.ui.nabu.casa"
    /// → ws(s)://.../api/websocket. Null se não for um endereço válido.
    /// </summary>
    public static Uri? ToWebSocket(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var scheme = u.Scheme switch
        {
            "http" or "ws" => "ws",
            "https" or "wss" => "wss",
            _ => null,
        };
        if (scheme is null || string.IsNullOrEmpty(u.Host)) return null;
        var b = new UriBuilder(u) { Scheme = scheme, Path = "/api/websocket", Query = "", Fragment = "" };
        if (u.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }
}
