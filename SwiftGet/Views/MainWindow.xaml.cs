using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwiftGet.Core;
using SwiftGet.Models;
using WinForms = System.Windows.Forms;

namespace SwiftGet.Views;

/// <summary>An entry in the sidebar: a section header or a filter.</summary>
public sealed class NavEntry : INotifyPropertyChanged
{
    int _count;

    public string TitleKey { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Brush? GlyphBrush { get; init; }
    public bool IsHeader { get; init; }
    public Func<DownloadItem, bool> Match { get; init; } = _ => true;

    public string Title => Loc.T(TitleKey);
    public string CountText => IsHeader || _count == 0 ? "" : _count.ToString();

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public void RefreshTitle() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly DownloadManager _m;
    readonly ListCollectionView _view;
    readonly List<NavEntry> _nav = new();
    readonly Dictionary<Guid, DownloadDetailsWindow> _details = new();
    WinForms.NotifyIcon? _tray;
    bool _exiting, _trayHintShown, _bridgeOk;
    string _search = "";

    public MainWindow(DownloadManager manager)
    {
        InitializeComponent();
        _m = manager;
        Icon = AppIcon.ImageSource;
        LogoImage.Source = AppIcon.LargeImageSource;

        _view = new ListCollectionView(_m.Items) { IsLiveFiltering = true, IsLiveSorting = true };
        foreach (var p in new[] { nameof(DownloadItem.Status), nameof(DownloadItem.InQueue), nameof(DownloadItem.Category), nameof(DownloadItem.FileName) })
            _view.LiveFilteringProperties.Add(p);
        _view.Filter = Filter;
        DownloadList.ItemsSource = _view;

        BuildNav();
        BuildSortBox();

        _m.ItemCompleted += OnItemCompleted;
        _m.Notify += ShowBalloon;
        _m.QueueStateChanged += UpdateStatus;
        _m.Items.CollectionChanged += (_, _) => UpdateStatus();
        Loc.I.LanguageChanged += OnLanguageChanged;
        ThemeManager.Changed += UpdateStatus;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();

        SetupTray();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ sidebar, sort, filter

    void BuildNav()
    {
        _nav.Add(new NavEntry { TitleKey = "NavStatus", IsHeader = true });
        _nav.Add(new NavEntry { TitleKey = "NavAll", Glyph = "" });
        _nav.Add(new NavEntry { TitleKey = "NavActive", Glyph = "", Match = i => i.IsRunning });
        _nav.Add(new NavEntry { TitleKey = "NavUnfinished", Glyph = "", Match = i => i.Status != DownloadStatus.Completed });
        _nav.Add(new NavEntry { TitleKey = "NavFinished", Glyph = "", Match = i => i.Status == DownloadStatus.Completed });
        _nav.Add(new NavEntry { TitleKey = "NavQueue", Glyph = "", Match = i => i.InQueue });
        _nav.Add(new NavEntry { TitleKey = "NavCategories", IsHeader = true });
        foreach (var c in Categories.All)
        {
            var key = c.Key;
            _nav.Add(new NavEntry { TitleKey = "Cat" + key, Glyph = c.Glyph, GlyphBrush = c.Brush, Match = i => i.Category == key });
        }
        NavList.ItemsSource = _nav;
        NavList.SelectedIndex = 1;
    }

    void BuildSortBox()
    {
        int index = Math.Max(0, SortBox.SelectedIndex);
        SortBox.ItemsSource = new[] { Loc.T("SortNewest"), Loc.T("SortName"), Loc.T("SortSize") };
        SortBox.SelectedIndex = index;
    }

    bool Filter(object o)
    {
        var item = (DownloadItem)o;
        if (NavList.SelectedItem is NavEntry nav && !nav.Match(item)) return false;
        return _search.Length == 0 || item.FileName.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _view.Refresh();
        UpdateStatus();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateStatus();
    }

    void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view == null) return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.LiveSortingProperties.Clear();
            var (prop, dir) = SortBox.SelectedIndex switch
            {
                1 => (nameof(DownloadItem.FileName), ListSortDirection.Ascending),
                2 => (nameof(DownloadItem.TotalSize), ListSortDirection.Descending),
                _ => (nameof(DownloadItem.DateAdded), ListSortDirection.Descending),
            };
            _view.SortDescriptions.Add(new SortDescription(prop, dir));
            _view.LiveSortingProperties.Add(prop);
        }
    }

    void OnLanguageChanged()
    {
        foreach (var n in _nav) n.RefreshTitle();
        BuildSortBox();
        RebuildTrayMenu();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ status

    public void SetBridgeStatus(bool ok)
    {
        _bridgeOk = ok;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        foreach (var n in _nav.Where(n => !n.IsHeader))
            n.Count = _m.Items.Count(n.Match);

        var nav = NavList.SelectedItem as NavEntry;
        PageTitle.Text = nav?.Title ?? "";
        var shown = _view.Count;
        PageCount.Text = Loc.F("ItemsCount", shown);
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

        SpeedStatus.Text = $"{Loc.T("TotalSpeed")}: {Format.Speed(_m.TotalSpeed)}";
        ActiveStatus.Text = Loc.F("ActiveDownloads", _m.ActiveCount);
        QueueStatus.Text = _m.QueueRunning ? Loc.T("QueueRunning") : Loc.T("QueueStopped");
        QueueDot.Fill = (Brush)FindResource(_m.QueueRunning ? "Success" : "TextTertiary");
        StartQueueButton.IsEnabled = !_m.QueueRunning;
        StopQueueButton.IsEnabled = _m.QueueRunning;

        BridgeText.Text = Loc.T(_bridgeOk ? "BrowserConnected" : "BrowserOff");
        BridgeDot.Fill = (Brush)FindResource(_bridgeOk ? "Success" : "Danger");

        if (_tray != null)
        {
            var tip = _m.ActiveCount > 0 ? $"SwiftGet — {Loc.F("ActiveDownloads", _m.ActiveCount)}" : "SwiftGet";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        }
    }

    // ------------------------------------------------------------------ browser and completion

    public void HandleBrowserRequest(BridgeRequest r)
    {
        if (_m.Settings.ShowDialogForBrowser)
        {
            var w = new AddDownloadWindow(_m, r.Url, r.Referrer, r.Cookies, r.UserAgent, r.FileName) { Topmost = true };
            w.Show();
            w.Activate();
        }
        else
        {
            var item = _m.CreateItem(r.Url, r.FileName, r.Referrer, r.Cookies, r.UserAgent);
            _m.Add(item);
            _m.Start(item);
            ShowBalloon(Loc.T("NotifyNew"), item.FileName);
        }
    }

    void OnItemCompleted(DownloadItem item)
    {
        if (_m.Settings.ShowCompleteDialog) new CompleteWindow(item).Show();
        else ShowBalloon(Loc.T("NotifyComplete"), item.FileName);
    }

    // ------------------------------------------------------------------ commands

    List<DownloadItem> Selected() => DownloadList.SelectedItems.Cast<DownloadItem>().ToList();

    void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        string? url = null;
        try
        {
            var text = Clipboard.GetText().Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                url = text;
        }
        catch { }
        new AddDownloadWindow(_m, url) { Owner = IsVisible ? this : null }.Show();
    }

    void Resume_Click(object sender, RoutedEventArgs e) { foreach (var i in Selected()) _m.Start(i); }
    void Pause_Click(object sender, RoutedEventArgs e) { foreach (var i in Selected()) _m.Pause(i); }
    void PauseAll_Click(object sender, RoutedEventArgs e) => _m.PauseAll();
    void StartQueue_Click(object sender, RoutedEventArgs e) => _m.StartQueue();
    void StopQueue_Click(object sender, RoutedEventArgs e) => _m.StopQueue();
    void Schedule_Click(object sender, RoutedEventArgs e) => OpenSettings(2);
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(0);

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count > 0 && Dialogs.Ask(this, Loc.T("ConfirmRestart")))
            foreach (var i in items) _m.Restart(i);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count > 0 && Dialogs.Ask(this, Loc.F("ConfirmDelete", items.Count)))
            foreach (var i in items) _m.Remove(i, deleteCompletedFile: false);
    }

    void DeleteWithFile_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count > 0 && Dialogs.Ask(this, Loc.F("ConfirmDeleteFile", items.Count)))
            foreach (var i in items) _m.Remove(i, deleteCompletedFile: true);
    }

    void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected().Where(i => i.Status != DownloadStatus.Completed)) _m.SetQueued(i, true);
    }

    void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected()) _m.SetQueued(i, false);
    }

    void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var links = string.Join(Environment.NewLine, Selected().Select(i => i.Url));
        if (links.Length > 0) try { Clipboard.SetText(links); } catch { }
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadList.SelectedItem is DownloadItem i) Shell.OpenFile(i.FullPath);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadList.SelectedItem is DownloadItem i) Shell.ShowInFolder(i.FullPath, i.SaveFolder);
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadList.SelectedItem is DownloadItem i) ShowDetails(i);
    }

    // row buttons
    static DownloadItem? RowItem(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    void RowPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is not { } i) return;
        if (_m.IsRunning(i)) _m.Pause(i);
        else if (i.Status == DownloadStatus.Completed) Shell.OpenFile(i.FullPath);
        else _m.Start(i);
    }

    void RowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } i) Shell.ShowInFolder(i.FullPath, i.SaveFolder);
    }

    void RowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } i) ShowDetails(i);
    }

    void DownloadList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
        if (ItemsControl.ContainerFromElement(DownloadList, e.OriginalSource as DependencyObject) is not ListViewItem row ||
            row.DataContext is not DownloadItem item) return;
        if (item.Status == DownloadStatus.Completed && File.Exists(item.FullPath)) Shell.OpenFile(item.FullPath);
        else ShowDetails(item);
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { AddUrl_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.Delete && DownloadList.IsKeyboardFocusWithin) { Delete_Click(this, e); e.Handled = true; }
    }

    public void OpenSettings(int page)
    {
        var w = new SettingsWindow(_m.Settings, page) { Owner = IsVisible ? this : null };
        if (w.ShowDialog() != true || w.Result == null) return;
        var s = w.Result;
        bool themeChanged = s.Theme != _m.Settings.Theme;
        bool langChanged = s.Language != _m.Settings.Language;
        _m.ApplySettings(s);
        if (s.StartWithWindows != Autostart.IsEnabled) Autostart.Set(s.StartWithWindows);
        if (themeChanged) ThemeManager.Apply(s.Theme);
        if (langChanged) Loc.I.SetLanguage(s.Language);
    }

    void ShowDetails(DownloadItem item)
    {
        if (_details.TryGetValue(item.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var w = new DownloadDetailsWindow(_m, item);
        _details[item.Id] = w;
        w.Closed += (_, _) => _details.Remove(item.Id);
        w.Show();
    }

    // ------------------------------------------------------------------ tray

    void SetupTray()
    {
        _tray = new WinForms.NotifyIcon { Icon = AppIcon.Icon, Text = "SwiftGet", Visible = true };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        RebuildTrayMenu();
    }

    void RebuildTrayMenu()
    {
        if (_tray == null) return;
        var menu = new WinForms.ContextMenuStrip
        {
            RightToLeft = Loc.I.IsRtl ? WinForms.RightToLeft.Yes : WinForms.RightToLeft.No
        };
        menu.Items.Add(Loc.T("TrayShow"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Loc.T("TrayAdd"), null, (_, _) => { ShowFromTray(); AddUrl_Click(this, new RoutedEventArgs()); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("StartQueue"), null, (_, _) => _m.StartQueue());
        menu.Items.Add(Loc.T("StopQueue"), null, (_, _) => _m.StopQueue());
        menu.Items.Add(Loc.T("StopAll"), null, (_, _) => _m.PauseAll());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("TrayExit"), null, (_, _) => ExitApp());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // brings it in front even when another app has focus
        Topmost = false;
    }

    void ShowBalloon(string title, string text) =>
        _tray?.ShowBalloonTip(4000, title, string.IsNullOrEmpty(text) ? " " : text, WinForms.ToolTipIcon.Info);

    void ExitApp()
    {
        _exiting = true;
        ((App)Application.Current).ExitApp();
    }

    public void DisposeTray()
    {
        if (_tray == null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                ShowBalloon("SwiftGet", Loc.T("TrayHint"));
                _trayHintShown = true;
            }
        }
        base.OnClosing(e);
    }
}
