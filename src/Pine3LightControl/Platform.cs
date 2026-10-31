using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Pine3LightControl.Core;

namespace Pine3LightControl;

/// <summary>Arranque com o Windows: entrada em HKCU\...\Run (não precisa de admin).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pine3LightControl";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static (bool Ok, string Message) Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue(ValueName, throwOnMissingValue: false);
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
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
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"[Shell] {target}: {e.Message}"); }
    }
}
