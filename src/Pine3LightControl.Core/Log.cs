namespace Pine3LightControl.Core;

/// <summary>Log simples em ficheiro (rodado a 1 MB) + evento para a UI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static event Action<string>? Written;

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var f = new FileInfo(AppPaths.LogFile);
                if (f.Exists && f.Length > 1_000_000)
                    File.Move(f.FullName, f.FullName + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
            catch { /* o log nunca pode derrubar a app */ }
        }
        try { Written?.Invoke(line); } catch { }
    }
}
