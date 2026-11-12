using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D = System.Drawing;

namespace SwiftGet.Views;

/// <summary>App icon from Assets/SwiftGet.ico and Assets/logo.png (embedded resources).</summary>
public static class AppIcon
{
    static D.Icon? _icon;
    static ImageSource? _small, _large;

    /// <summary>Small icon for the notification area.</summary>
    public static D.Icon Icon
    {
        get
        {
            if (_icon != null) return _icon;
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SwiftGet.ico"))!.Stream;
            return _icon = new D.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
    }

    /// <summary>Window title-bar icon.</summary>
    public static ImageSource ImageSource =>
        _small ??= BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SwiftGet.ico"));

    /// <summary>Large logo for headers.</summary>
    public static ImageSource LargeImageSource
    {
        get
        {
            if (_large != null) return _large;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/Assets/logo.png");
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return _large = img;
        }
    }
}
