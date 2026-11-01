using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pine3LightControl.Core;

public enum AppearanceMode { System, Light, Dark }

public sealed class HomeAssistantConfig
{
    /// <summary>Ex.: http://homeassistant.local:8123</summary>
    public string Url { get; set; } = "";
    /// <summary>Long-lived access token, cifrado com DPAPI (ver <see cref="Secret"/>).</summary>
    public string TokenProtected { get; set; } = "";
    /// <summary>Aceitar certificados autoassinados (HA local em https).</summary>
    public bool AllowInvalidCertificate { get; set; }
    /// <summary>Entidades escolhidas para aparecer no painel, pela ordem.</summary>
    public List<string> Entities { get; set; } = new();

    [JsonIgnore]
    public string Token
    {
        get => Secret.Unprotect(TokenProtected);
        set => TokenProtected = Secret.Protect(value);
    }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrEmpty(TokenProtected);
}

/// <summary>RGB do PC controlado diretamente pela app (sem OpenRGB).</summary>
public sealed class PcRgbConfig
{
    /// <summary>Logótipo Radeon (USB, não precisa de administrador).</summary>
    public bool Gpu { get; set; } = true;
    /// <summary>Memórias Corsair por SMBus (PawnIO + administrador). Desligado por omissão.</summary>
    public bool Memory { get; set; }
    /// <summary>Rato Logitech (HID++ pelo recetor LIGHTSPEED).</summary>
    public bool Mouse { get; set; } = true;
    /// <summary>Teclado Ducky OK-M 75 (VIA).</summary>
    public bool Keyboard { get; set; } = true;
}

/// <summary>Ligação ao servidor SDK do OpenRGB (gráfica, memórias, periféricos...).</summary>
public sealed class OpenRgbConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = OpenRgb.OpenRgbProtocol.DefaultPort;
    /// <summary>Grupos (nome do dispositivo) a mostrar no painel, pela ordem.</summary>
    public List<string> Devices { get; set; } = new();
}

/// <summary>Definições persistidas em %APPDATA%\Pine3LightControl\config.json.</summary>
public sealed class AppConfig
{
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public OpenRgbConfig OpenRgb { get; set; } = new();
    public PcRgbConfig PcRgb { get; set; } = new();
    /// <summary>Cenas: estado guardado de várias luzes, ativado de uma vez.</summary>
    public List<Scenes.Scene> Scenes { get; set; } = new();

    // ── Persistência ─────────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object Gate = new();

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Json) ?? new();
        }
        catch (Exception e) { Log.Write($"[Config] erro a ler: {e.Message}"); }
        return new AppConfig();
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var tmp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
            }
            catch (Exception e) { Log.Write($"[Config] erro a gravar: {e.Message}"); }
        }
    }
}

/// <summary>
/// Proteção de segredos. Por defeito não faz nada (testes, Linux); a app Windows
/// liga aqui o DPAPI (CurrentUser) no arranque.
/// </summary>
public static class Secret
{
    public static Func<string, string> Protect { get; set; } = s => s;
    public static Func<string, string> Unprotect { get; set; } = s => s;
}
