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

/// <summary>Definições persistidas em %APPDATA%\Pine3LightControl\config.json.</summary>
public sealed class AppConfig
{
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
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
