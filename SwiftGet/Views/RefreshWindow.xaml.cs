using System.Windows;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>Give an expired download a new address and continue where it stopped.</summary>
public partial class RefreshWindow : Window
{
    readonly DownloadManager _m;
    readonly DownloadItem _item;

    /// <summary>Raised when the user chooses to catch the next browser download for this item.</summary>
    public event Action<DownloadItem>? WaitForBrowser;

    public RefreshWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        _item = item;
        FileText.Text = item.FileName;
        OldBox.Text = item.Url;
        try
        {
            var clip = Clipboard.GetText().Trim();
            if (Uri.TryCreate(clip, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && clip != item.Url) NewBox.Text = clip;
        }
        catch { }
        Loaded += (_, _) => NewBox.Focus();
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var url = NewBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.Scheme is "http" or "https" or "ftp"))
        {
            Show(Loc.T("ProbeInvalidUrl"));
            return;
        }
        ApplyButton.IsEnabled = false;
        var error = await _m.RefreshAddressAsync(_item, url);
        ApplyButton.IsEnabled = true;
        if (error != null) Show(error);
        else Close();
    }

    void Wait_Click(object sender, RoutedEventArgs e)
    {
        WaitForBrowser?.Invoke(_item);
        Close();
    }

    void Show(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
