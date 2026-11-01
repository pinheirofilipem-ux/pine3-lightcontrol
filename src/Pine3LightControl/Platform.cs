using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Pine3LightControl.Core;

namespace Pine3LightControl;

/// <summary>
/// Arranque com o Windows: tarefa agendada "ao iniciar sessão" com privilégios
/// máximos (como o Pine3 Monitor). A app corre como administrador por causa do
/// SMBus das memórias, e a chave Run não arranca apps que pedem administrador.
/// </summary>
internal static class StartupManager
{
    public const string TaskName = "Pine3 Light Control";
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "Pine3LightControl";

    public static bool IsEnabled()
    {
        var (code, _) = Shell.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static (bool Ok, string Message) Set(bool enabled) =>
        enabled ? Enable(Environment.ProcessPath ?? "") : Disable();

    public static (bool Ok, string Message) Enable(string exePath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Pine3 Light Control at logon.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>--minimized</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        // ExecutionTimeLimit PT0S: sem isto o Windows mata a app ao fim de 72 h.
        var tmp = Path.Combine(Path.GetTempPath(), "pine3lightcontrol-task.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var (code, output) = Shell.Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            if (code != 0) return (false, output);
            RemoveLegacyRunEntry();
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static (bool Ok, string Message) Disable()
    {
        RemoveLegacyRunEntry();
        if (!IsEnabled()) return (true, "");
        var (code, output) = Shell.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        return (code == 0, output);
    }

    /// <summary>Versões ≤ 0.3 arrancavam pela chave Run (sem administrador).</summary>
    public static void RemoveLegacyRunEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            k?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch { }
    }
}

/// <summary>DPAPI (utilizador atual) para o token do Home Assistant.</summary>
internal static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Pine3LightControl");

    public static void Install()
    {
        Secret.Protect = s => string.IsNullOrEmpty(s) ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), Entropy, DataProtectionScope.CurrentUser));
        Secret.Unprotect = s =>
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), Entropy, DataProtectionScope.CurrentUser)); }
            catch (Exception e) { Log.Write($"[Config] token ilegível: {e.Message}"); return ""; }
        };
    }
}

internal static class Shell
{
    /// <summary>
    /// Abre URL/pasta através do explorer.exe, para o browser não abrir como
    /// administrador (a app corre elevada).
    /// </summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false }); }
        catch (Exception e) { Log.Write($"[Shell] {target}: {e.Message}"); }
    }

    public static (int Code, string Output) Run(string file, string args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "timeout");
            }
            return (p.ExitCode, (o.Result + e.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
