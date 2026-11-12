using System.Windows;
using Microsoft.Win32;

namespace SwiftGet.Core;

/// <summary>Switches between the light and dark palettes (Themes/*.xaml) and the matching Windows 11 control style.</summary>
public static class ThemeManager
{
    static string _mode = "System";

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(string? mode)
    {
        _mode = string.IsNullOrEmpty(mode) ? "System" : mode;
        bool dark = _mode == "Dark" || (_mode != "Light" && SystemUsesDarkTheme());
        IsDark = dark;

        var app = Application.Current;
#pragma warning disable WPF0001 // Fluent theme is marked experimental
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SwiftGet;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
        };
        var merged = app.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/") == true &&
                                             d.Source.OriginalString.Contains("SwiftGet;component"));
        if (old != null) merged.Remove(old);
        merged.Add(palette);
        Changed?.Invoke();
    }

    /// <summary>Follow Windows when it switches between light and dark.</summary>
    public static void WatchSystem()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == "System")
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (SystemUsesDarkTheme() != IsDark) Apply(_mode);
                });
        };
    }

    static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Adds or removes SwiftGet from HKCU\...\Run.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SwiftGet";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is string exe)
                key.SetValue(Name, $"\"{exe}\" --minimized");
            else
                key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch
        {
            // not critical
        }
    }
}
