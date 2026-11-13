using System.Collections.ObjectModel;
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

/// <summary>A node in the category tree.</summary>
public sealed class NavEntry : INotifyPropertyChanged
{
    int _count;
    bool _expanded = true, _selected;

    public string TitleKey { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Brush? GlyphBrush { get; init; }
    public Func<DownloadItem, bool> Match { get; init; } = _ => true;
    public ObservableCollection<NavEntry> Children { get; } = new();

    public string Title => Loc.T(TitleKey);
    public string CountText => _count == 0 ? "" : $"({_count})";

    public int Count
    {
        get => _count;
        set { if (_count != value) { _count = value; Raise(nameof(CountText)); } }
    }

    public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(nameof(IsExpanded)); } }
    public bool IsSelected { get => _selected; set { _selected = value; Raise(nameof(IsSelected)); } }

    public IEnumerable<NavEntry> SelfAndDescendants() => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));
    public void RefreshTitle() => Raise(nameof(Title));

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly DownloadManager _m;
    readonly ListCollectionView _view;
    readonly List<NavEntry> _roots = new();
    readonly Dictionary<Guid, DownloadDetailsWindow> _details = new();
    NavEntry? _selectedNav;
    WinForms.NotifyIcon? _tray;
    bool _exiting, _trayHintShown, _bridgeOk;
    string _search = "";

    public MainWindow(DownloadManager manager)
    {
        InitializeComponent();
        _m = manager;
        Icon = AppIcon.ImageSource;

        _view = new ListCollectionView(_m.Items) { IsLiveFiltering = true };
        foreach (var p in new[] { nameof(DownloadItem.Status), nameof(DownloadItem.InQueue), nameof(DownloadItem.Category), nameof(DownloadItem.FileName) })
            _view.LiveFilteringProperties.Add(p);
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.DateAdded), ListSortDirection.Descending));
        _view.Filter = Filter;
        DownloadGrid.ItemsSource = _view;

        BuildNav();

        _m.ItemCompleted += OnItemCompleted;
        _m.ProgressRequested += ShowDetails;
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

    // ------------------------------------------------------------------ category tree and filter

    NavEntry CategoryNode(CategoryInfo c, Func<DownloadItem, bool> parent)
    {
        var key = c.Key;
        return new NavEntry { TitleKey = "Cat" + key, Glyph = c.Glyph, GlyphBrush = c.Brush, Match = i => parent(i) && i.Category == key };
    }

    void BuildNav()
    {
        var all = new NavEntry { TitleKey = "NavAll", Glyph = "" };
        foreach (var c in Categories.All) all.Children.Add(CategoryNode(c, _ => true));

        Func<DownloadItem, bool> unfinished = i => i.Status != DownloadStatus.Completed;
        var unfinishedNode = new NavEntry { TitleKey = "NavUnfinished", Glyph = "", Match = unfinished, IsExpanded = false };
        foreach (var c in Categories.All) unfinishedNode.Children.Add(CategoryNode(c, unfinished));

        Func<DownloadItem, bool> finished = i => i.Status == DownloadStatus.Completed;
        var finishedNode = new NavEntry { TitleKey = "NavFinished", Glyph = "", Match = finished, IsExpanded = false };
        foreach (var c in Categories.All) finishedNode.Children.Add(CategoryNode(c, finished));

        var queueNode = new NavEntry { TitleKey = "NavQueue", Glyph = "", GlyphBrush = (Brush)FindResource("Accent"), Match = i => i.InQueue };

        _roots.AddRange(new[] { all, unfinishedNode, finishedNode, queueNode });
        NavTree.ItemsSource = _roots;
        all.IsSelected = true;
        _selectedNav = all;
    }

    IEnumerable<NavEntry> AllNav() => _roots.SelectMany(r => r.SelfAndDescendants());

    bool Filter(object o)
    {
        var item = (DownloadItem)o;
        if (_selectedNav != null && !_selectedNav.Match(item)) return false;
        return _search.Length == 0 || item.FileName.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNav = e.NewValue as NavEntry;
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

    void OnLanguageChanged()
    {
        foreach (var n in AllNav()) n.RefreshTitle();
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
        foreach (var n in AllNav()) n.Count = _m.Items.Count(n.Match);

        SpeedStatus.Text = $"{Loc.T("TotalSpeed")}: {Format.Speed(_m.TotalSpeed)}";
        ActiveStatus.Text = Loc.F("ActiveDownloads", _m.ActiveCount);
        QueueStatus.Text = _m.QueueRunning ? Loc.T("QueueRunning") : Loc.T("QueueStopped");
        QueueDot.Fill = (Brush)FindResource(_m.QueueRunning ? "Success" : "TextTertiary");
        CountStatus.Text = Loc.F("ItemsCount", _view.Count);
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
            _m.StartWithProgress(item);
            if (!_m.Settings.ShowProgressWindow) ShowBalloon(Loc.T("NotifyNew"), item.FileName);
        }
    }

    void OnItemCompleted(DownloadItem item)
    {
        if (_details.TryGetValue(item.Id, out var progress)) progress.Close();
        if (_m.Settings.ShowCompleteDialog) new CompleteWindow(item).Show();
        else ShowBalloon(Loc.T("NotifyComplete"), item.FileName);
    }

    // ------------------------------------------------------------------ commands

    List<DownloadItem> Selected() => DownloadGrid.SelectedItems.Cast<DownloadItem>().ToList();

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

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        foreach (var i in items)
        {
            if (items.Count == 1) _m.StartWithProgress(i);
            else _m.Start(i);
        }
    }

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
        if (DownloadGrid.SelectedItem is DownloadItem i) Shell.OpenFile(i.FullPath);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadGrid.SelectedItem is DownloadItem i) Shell.ShowInFolder(i.FullPath, i.SaveFolder);
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadGrid.SelectedItem is DownloadItem i) ShowDetails(i);
    }

    void DownloadGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DownloadGrid, e.OriginalSource as DependencyObject) is not DataGridRow row ||
            row.Item is not DownloadItem item) return;
        if (item.Status == DownloadStatus.Completed && File.Exists(item.FullPath)) Shell.OpenFile(item.FullPath);
        else ShowDetails(item);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { AddUrl_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.Delete && DownloadGrid.IsKeyboardFocusWithin) { Delete_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Enter && DownloadGrid.IsKeyboardFocusWithin && DownloadGrid.SelectedItem is DownloadItem i)
        {
            ShowDetails(i);
            e.Handled = true;
        }
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

    public void ShowDetails(DownloadItem item)
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
