using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

public partial class DownloadDetailsWindow : Window
{
    sealed record PartRow(string Name, double Percent, string SizeText, string State, Brush StateBrush);

    readonly DownloadManager _m;
    readonly DownloadItem _item;
    readonly DispatcherTimer _timer;

    public DownloadDetailsWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        _item = item;
        DataContext = item;
        Bar.Item = item;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    void Refresh()
    {
        Title = $"{_item.Progress:0}% · {_item.FileName}";
        PercentText.Text = $"{_item.Progress:0.0}%";
        ErrorText.Visibility = string.IsNullOrEmpty(_item.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
        Bar.InvalidateVisual();

        var active = (Brush)FindResource("Accent");
        var done = (Brush)FindResource("Success");
        var idle = (Brush)FindResource("TextTertiary");
        PartList.ItemsSource = _item.SnapshotSegments().Select((s, i) =>
        {
            double pct = s.Length > 0 ? Math.Min(100, s.Downloaded * 100.0 / s.Length) : 0;
            var (state, brush) = s.Active ? (Loc.T("PartActive"), active)
                : s.Remaining <= 0 ? (Loc.T("PartDone"), done)
                : (Loc.T("PartWaiting"), idle);
            return new PartRow(Loc.F("PartLine", i + 1), pct, Loc.F("SizeOf", Format.Size(s.Downloaded), Format.Size(s.Length)), state, brush);
        }).ToList();

        bool running = _m.IsRunning(_item);
        ToggleGlyph.Text = running ? "" : "";
        ToggleText.Text = Loc.T(running ? "Pause" : "Resume");
        ToggleButton.IsEnabled = _item.Status != DownloadStatus.Completed;
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_m.IsRunning(_item)) _m.Pause(_item);
        else _m.Start(_item);
        Refresh();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Shell.ShowInFolder(_item.FullPath, _item.SaveFolder);
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
