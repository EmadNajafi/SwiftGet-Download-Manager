using System.Windows;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>Small window in the corner of the screen when a download finishes.</summary>
public partial class CompleteWindow : Window
{
    readonly DownloadItem _item;

    public CompleteWindow(DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _item = item;
        DataContext = item;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        Shell.OpenFile(_item.FullPath);
        Close();
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        Shell.ShowInFolder(_item.FullPath, _item.SaveFolder);
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
