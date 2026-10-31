namespace Pine3LightControl.Core;

/// <summary>Pastas e ficheiros da app (%APPDATA%\Pine3LightControl).</summary>
public static class AppPaths
{
    /// <summary>Pode ser redefinida (testes).</summary>
    public static string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pine3LightControl");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogFile => Path.Combine(DataDir, "pine3lightcontrol.log");
}
