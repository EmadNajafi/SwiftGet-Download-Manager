using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>Progress window: information, one smooth progress bar, and the connection table.</summary>
public partial class DownloadDetailsWindow : Window
{
    /// <summary>One row of the connection table; updated in place so the list doesn't flicker.</summary>
    public sealed class PartRow : INotifyPropertyChanged
    {
        string _size = "", _state = "";
        Brush? _brush;

        public int Number { get; init; }
        public string SizeText { get => _size; set { if (_size != value) { _size = value; Raise(nameof(SizeText)); } } }
        public string State { get => _state; set { if (_state != value) { _state = value; Raise(nameof(State)); } } }
        public Brush? StateBrush { get => _brush; set { if (_brush != value) { _brush = value; Raise(nameof(StateBrush)); } } }

        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    static bool _showDetails = true;   // remembered between windows, like a classic download manager

    readonly DownloadManager _m;
    readonly DownloadItem _item;
    readonly DispatcherTimer _timer;
    readonly ObservableCollection<PartRow> _parts = new();
    double _shownProgress = -1;

    public DownloadDetailsWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        _item = item;
        DataContext = item;
        Bar.Item = item;
        PartList.ItemsSource = _parts;
        ApplyDetailsVisibility();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    void Refresh()
    {
        var progress = _item.Progress;
        Title = $"{progress:0}%  {_item.FileName}";
        PercentText.Text = $"{progress:0.00}%";
        AnimateBar(progress);

        StatusValue.Text = _item.Status switch
        {
            DownloadStatus.Downloading => Loc.T("PdReceiving"),
            DownloadStatus.Error => _item.ErrorMessage ?? Loc.T("StatusError"),
            _ => _item.StatusText,
        };
        StatusValue.Foreground = (Brush)FindResource(_item.Status switch
        {
            DownloadStatus.Error => "Danger",
            DownloadStatus.Completed => "Success",
            DownloadStatus.Downloading => "Accent",
            _ => "Text",
        });
        ResumeValue.Text = Loc.T(_item.ResumeSupported ? "Yes" : "No");
        MainBar.Foreground = (Brush)FindResource(_item.Status == DownloadStatus.Error ? "Danger" : "Success");

        bool running = _m.IsRunning(_item);
        ToggleGlyph.Text = running ? "" : "";
        ToggleText.Text = Loc.T(running ? "Pause" : "Resume");
        ToggleButton.IsEnabled = _item.Status != DownloadStatus.Completed;

        if (DetailsPanel.Visibility == Visibility.Visible)
        {
            Bar.InvalidateVisual();
            UpdateParts();
        }
    }

    /// <summary>Glides the bar to the new value instead of jumping.</summary>
    void AnimateBar(double target)
    {
        if (Math.Abs(target - _shownProgress) < 0.01) return;
        var anim = new DoubleAnimation(target, _shownProgress < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        MainBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
        _shownProgress = target;
    }

    void UpdateParts()
    {
        var segs = _item.SnapshotSegments();
        while (_parts.Count > segs.Count) _parts.RemoveAt(_parts.Count - 1);
        var active = (Brush)FindResource("Accent");
        var done = (Brush)FindResource("Success");
        var idle = (Brush)FindResource("TextTertiary");
        bool running = _m.IsRunning(_item);

        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (i >= _parts.Count) _parts.Add(new PartRow { Number = i + 1 });
            var row = _parts[i];
            row.SizeText = s.Length > 0 ? Loc.F("SizeOf", Format.Size(s.Downloaded), Format.Size(s.Length)) : Format.Size(s.Downloaded);
            (row.State, row.StateBrush) = s.Remaining <= 0 ? (Loc.T("PartDone"), done)
                : s.Active ? (Loc.T("PdReceiving"), active)
                : (running ? Loc.T("PartWaiting") : _item.StatusText, idle);
        }
    }

    void ApplyDetailsVisibility()
    {
        DetailsPanel.Visibility = _showDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = Loc.T(_showDetails ? "HideDetails" : "ShowDetails");
    }

    void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _showDetails = !_showDetails;
        ApplyDetailsVisibility();
        Refresh();
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_m.IsRunning(_item)) _m.Pause(_item);
        else _m.Start(_item);
        Refresh();
    }

    /// <summary>Cancel stops the download (it can be resumed later) and closes the window.</summary>
    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_m.IsRunning(_item)) _m.Pause(_item);
        Close();
    }
}
