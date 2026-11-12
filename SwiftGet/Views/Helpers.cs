using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SwiftGet.Core;

namespace SwiftGet.Views;

public sealed class InverseBoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class Dialogs
{
    static MessageBoxOptions Options => Loc.I.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    public static bool Ask(Window? owner, string text)
    {
        var r = owner != null && owner.IsVisible
            ? MessageBox.Show(owner, text, "SwiftGet", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, Options)
            : MessageBox.Show(text, "SwiftGet", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, Options);
        return r == MessageBoxResult.Yes;
    }

    public static void Info(Window? owner, string text, bool warning = false)
    {
        var icon = warning ? MessageBoxImage.Warning : MessageBoxImage.Information;
        if (owner != null && owner.IsVisible)
            MessageBox.Show(owner, text, "SwiftGet", MessageBoxButton.OK, icon, MessageBoxResult.OK, Options);
        else
            MessageBox.Show(text, "SwiftGet", MessageBoxButton.OK, icon, MessageBoxResult.OK, Options);
    }
}

public static class Shell
{
    public static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public static void ShowInFolder(string file, string folder)
    {
        try
        {
            if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
