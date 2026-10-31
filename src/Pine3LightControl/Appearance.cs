using System.Windows;
using Microsoft.Win32;
using Pine3LightControl.Core;

namespace Pine3LightControl;

/// <summary>Tema Fluent do WPF (.NET 9+): claro, escuro ou segue o Windows.</summary>
internal static class Appearance
{
    public static void Apply(AppearanceMode mode)
    {
        Application.Current.ThemeMode = mode switch
        {
            AppearanceMode.Light => ThemeMode.Light,
            AppearanceMode.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    public static bool IsDark(AppearanceMode mode)
    {
        if (mode == AppearanceMode.Dark) return true;
        if (mode == AppearanceMode.Light) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
