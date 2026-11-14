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

    public Func<string>? TitleFunc { get; init; }
    public DownloadQueue? Queue { get; init; }
    public string Title => TitleFunc?.Invoke() ?? Loc.T(TitleKey);
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
    public override string ToString() => Title;   // read by screen readers

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
        foreach (var p in new[] { nameof(DownloadItem.Status), nameof(DownloadItem.InQueue), nameof(DownloadItem.QueueId), nameof(DownloadItem.Category), nameof(DownloadItem.FileName) })
            _view.LiveFilteringProperties.Add(p);
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.DateAdded), ListSortDirection.Descending));
        _view.Filter = Filter;
        DownloadGrid.ItemsSource = _view;

        BuildNav();

        _m.ItemCompleted += OnItemCompleted;
        _m.ProgressRequested += ShowDetails;
        _m.QueueFinishActionRequested += (q, action) => new CountdownWindow(q, action).Show();
        _m.Queues.CollectionChanged += (_, _) => RebuildQueueNodes();
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
        StartClipboardMonitor();
    }

    // ------------------------------------------------------------------ clipboard monitor

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);
    const int WM_CLIPBOARDUPDATE = 0x031D;
    string _lastClipboard = "";
    DateTime _lastClipboardTime;

    /// <summary>Watches the clipboard; a copied link to a file opens the "New download" window (like IDM).</summary>
    void StartClipboardMonitor()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        AddClipboardFormatListener(hwnd);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _m.Settings.MonitorClipboard)
            Dispatcher.BeginInvoke(OnClipboardChanged, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    void OnClipboardChanged()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; } catch { return; }
        if (text.Length == 0 || text.Length > 2000 || text.Contains('\n')) return;
        if (text == _lastClipboard && DateTime.Now - _lastClipboardTime < TimeSpan.FromSeconds(30)) return;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !(uri.Scheme is "http" or "https" or "ftp")) return;
        var ext = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        var wanted = _m.Settings.ClipboardExtensions.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimStart('.').ToLowerInvariant());
        if (ext.Length == 0 || !wanted.Contains(ext)) return;
        if (_m.Items.Any(i => i.Url == text)) return;
        _lastClipboard = text;
        _lastClipboardTime = DateTime.Now;
        var w = new AddDownloadWindow(_m, text) { Topmost = true };
        w.Show();
        w.Activate();
    }

    // ------------------------------------------------------------------ menu commands

    void Batch_Click(object sender, RoutedEventArgs e) => new BatchWindow(_m) { Owner = IsVisible ? this : null }.ShowDialog();

    void Grabber_Click(object sender, RoutedEventArgs e)
    {
        string? url = null;
        try { var c = Clipboard.GetText().Trim(); if (Uri.TryCreate(c, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http")) url = c; } catch { }
        new GrabberWindow(_m, url) { Owner = IsVisible ? this : null }.ShowDialog();
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("ImportFilter") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                int added = _m.ImportList(dlg.FileName);
                Dialogs.Info(this, Loc.F("ImportDone", added));
            }
            else
            {
                var links = File.ReadAllLines(dlg.FileName).Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Select(l => new LinkCandidate(l)).ToList();
                new LinksWindow(_m, links, Loc.T("MnImport"), Path.GetFileName(dlg.FileName)) { Owner = this }.Show();
            }
        }
        catch (Exception ex) { Dialogs.Info(this, ex.Message, warning: true); }
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("ExportFilter"), FileName = "SwiftGet-downloads" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var items = Selected().Count > 1 ? Selected() : _m.Items.ToList();
            if (dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                File.WriteAllLines(dlg.FileName, items.Select(i => i.Url));
            else
                _m.ExportList(dlg.FileName, items);
            Dialogs.Info(this, Loc.F("ExportDone", items.Count));
        }
        catch (Exception ex) { Dialogs.Info(this, ex.Message, warning: true); }
    }

    void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _m.Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Queued).ToList())
            _m.Start(i);
    }

    void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var done = _m.Items.Where(i => i.Status == DownloadStatus.Completed).ToList();
        if (done.Count > 0 && Dialogs.Ask(this, Loc.F("ConfirmDeleteCompleted", done.Count)))
            foreach (var i in done) _m.Remove(i, deleteCompletedFile: false);
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadGrid.SelectedItem is not DownloadItem item || item.Status == DownloadStatus.Completed) return;
        var w = new RefreshWindow(_m, item) { Owner = this };
        w.WaitForBrowser += i =>
        {
            _pendingRefresh = i;
            ShowBalloon(Loc.T("RefreshTitle"), Loc.F("RefreshWaiting", i.FileName));
        };
        w.ShowDialog();
    }

    void SpeedLimit_Click(object sender, RoutedEventArgs e) => OpenSettings(1);
    void BrowserSetup_Click(object sender, RoutedEventArgs e) => OpenSettings(2);
    void About_Click(object sender, RoutedEventArgs e) => OpenSettings(5);
    void Project_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(App.ProjectUrl);
    void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();
    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(silent: false);

    // ------------------------------------------------------------------ updates

    static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
    UpdateInfo? _available;
    bool _updateNotified;
    DispatcherTimer? _updateTimer;

    /// <summary>
    /// Checks for a new version shortly after start and then every few hours while SwiftGet runs
    /// (it usually stays in the notification area for days).
    /// </summary>
    public void ScheduleUpdateCheck()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromMinutes(30);
            if (!_m.Settings.CheckForUpdates || DateTime.Now - _m.Settings.LastUpdateCheck < UpdateInterval) return;
            await CheckForUpdatesAsync(silent: true);
        };
        _updateTimer.Start();
    }

    async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var info = await Updater.CheckAsync(CancellationToken.None);
            _m.Settings.LastUpdateCheck = DateTime.Now;
            _m.ApplySettings(_m.Settings);
            if (info == null)
            {
                if (!silent) Dialogs.Info(this, Loc.F("UpdNone", Updater.Current));
                return;
            }
            if (!silent)
            {
                ShowUpdateWindow(info);
                return;
            }
            OfferUpdate(info);
        }
        catch (Exception ex)
        {
            if (!silent) Dialogs.Info(this, Loc.F("UpdFailed", ex.Message), warning: true);
        }
    }

    /// <summary>A new version was found in the background: banner in the window and a Windows notification.</summary>
    void OfferUpdate(UpdateInfo info)
    {
        bool sameAsBefore = _available?.Version == info.Version;
        _available = info;
        bool skipped = info.Version.ToString() == _m.Settings.SkippedVersion;
        if (skipped) return;

        UpdateBannerText.Text = Loc.F("UpdBanner", info.Version);
        UpdateBanner.Visibility = Visibility.Visible;
        RebuildTrayMenu();
        if (!sameAsBefore || !_updateNotified)
        {
            _updateNotified = true;
            ShowBalloon(Loc.T("UpdNotifyTitle"), Loc.F("UpdNotifyText", info.Version), () => ShowUpdateWindow(info));
        }
    }

    void ShowUpdateWindow(UpdateInfo info)
    {
        ShowFromTray();
        new UpdateWindow(_m, info) { Owner = this }.ShowDialog();
        // "Skip this version" hides the banner
        if (info.Version.ToString() == _m.Settings.SkippedVersion) UpdateBanner.Visibility = Visibility.Collapsed;
    }

    void UpdateNow_Click(object sender, RoutedEventArgs e) { if (_available != null) ShowUpdateWindow(_available); }
    void UpdateDismiss_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

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

        _queuesNode = new NavEntry { TitleKey = "NavQueues", Glyph = "", GlyphBrush = (Brush)FindResource("Accent"), Match = i => i.InQueue };
        RebuildQueueNodes();

        _roots.AddRange(new[] { all, unfinishedNode, finishedNode, _queuesNode });
        NavTree.ItemsSource = _roots;
        all.IsSelected = true;
        _selectedNav = all;
    }

    NavEntry? _queuesNode;

    void RebuildQueueNodes()
    {
        if (_queuesNode == null) return;
        var selectedQueue = _selectedNav?.Queue;
        _queuesNode.Children.Clear();
        foreach (var q in _m.Queues)
        {
            var queue = q;
            var node = new NavEntry
            {
                TitleFunc = () => queue.DisplayName,
                Queue = queue,
                Glyph = "",
                GlyphBrush = (Brush)FindResource("Accent"),
                Match = i => i.QueueId == queue.Id,
            };
            _queuesNode.Children.Add(node);
            if (queue == selectedQueue) node.IsSelected = true;
            queue.PropertyChanged -= OnQueueChanged;
            queue.PropertyChanged += OnQueueChanged;
        }
        UpdateStatus();
    }

    void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadQueue.DisplayName) or nameof(DownloadQueue.IsRunning))
            foreach (var n in _queuesNode?.Children ?? new()) n.RefreshTitle();
        if (e.PropertyName == nameof(DownloadQueue.IsRunning)) UpdateStatus();
    }

    /// <summary>The queue the toolbar's queue buttons act on: the selected queue, otherwise the main queue.</summary>
    DownloadQueue TargetQueue => _selectedNav?.Queue ?? _m.MainQueue;

    // ------------------------------------------------------------------ tree right-click menu

    /// <summary>Right-click selects the node under the mouse first, like Explorer.</summary>
    void NavTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    void NavTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        NavMenu.Items.Clear();
        var node = _selectedNav;
        if (node?.Queue is { } q)
        {
            AddMenu(Loc.T("StartQueue"), "\uE768", () => _m.StartQueue(q), !q.IsRunning);
            AddMenu(Loc.T("StopQueue"), "\uE71A", () => _m.StopQueue(q), q.IsRunning);
            NavMenu.Items.Add(new Separator());
            AddMenu(Loc.T("EditQueue"), "\uE70F", () => OpenScheduler(q));
            AddMenu(Loc.T("DeleteQueue"), "\uE74D", () =>
            {
                if (Dialogs.Ask(this, Loc.F("ConfirmDeleteQueue", q.DisplayName))) _m.DeleteQueue(q);
            }, !q.IsMain);
            NavMenu.Items.Add(new Separator());
            AddMenu(Loc.T("NewQueue"), "\uE710", NewQueue);
        }
        else if (node == _queuesNode)
        {
            AddMenu(Loc.T("NewQueue"), "\uE710", NewQueue);
            AddMenu(Loc.T("Scheduler") + "…", "\uE787", () => OpenScheduler());
        }
        else
        {
            e.Handled = true;   // no menu for category folders
        }
    }

    void AddMenu(string header, string glyph, Action action, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 14 },
        };
        mi.Click += (_, _) => { action(); UpdateStatus(); };
        NavMenu.Items.Add(mi);
    }

    void NewQueue()
    {
        int n = 1;
        while (_m.Queues.Any(x => x.Name == Loc.F("NewQueueName", n))) n++;
        var q = _m.CreateQueue(Loc.F("NewQueueName", n));
        OpenScheduler(q);
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
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            if (_selectedNav?.Queue != null)
                _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.QueueOrder), ListSortDirection.Ascending));
            else
                _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.DateAdded), ListSortDirection.Descending));
        }
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
        int running = _m.Queues.Count(q => q.IsRunning);
        QueueStatus.Text = running > 0 ? Loc.F("QueuesRunning", running) : Loc.T("QueueStopped");
        QueueDot.Fill = (Brush)FindResource(running > 0 ? "Success" : "TextTertiary");
        CountStatus.Text = Loc.F("ItemsCount", _view.Count);
        var target = TargetQueue;
        bool several = _m.Queues.Count > 1;
        StartQueueButton.IsEnabled = _m.Queues.Any(q => !q.IsRunning);
        StopQueueButton.IsEnabled = _m.Queues.Any(q => q.IsRunning);
        StartQueueArrow.Visibility = StopQueueArrow.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        StartQueueButton.ToolTip = several ? null : _m.MainQueue.DisplayName;

        var seen = BrowserBridge.LastExtensionContact;
        bool extension = seen != null && DateTime.Now - seen.Value < TimeSpan.FromMinutes(3);
        BridgeText.Text = Loc.T(!_bridgeOk ? "BrowserOff" : extension ? "ExtConnected" : "ExtNotConnected");
        BridgeDot.Fill = (Brush)FindResource(!_bridgeOk ? "Danger" : extension ? "Success" : "Warning");

        if (_tray != null)
        {
            var tip = _m.ActiveCount > 0 ? $"SwiftGet — {Loc.F("ActiveDownloads", _m.ActiveCount)}" : "SwiftGet";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        }
    }

    // ------------------------------------------------------------------ browser and completion

    DownloadItem? _pendingRefresh;

    public void HandleBrowserRequest(BridgeRequest r)
    {
        // "Refresh download address → catch from browser": this link replaces the expired one
        if (_pendingRefresh is { } waiting)
        {
            _pendingRefresh = null;
            if (Dialogs.Ask(null, Loc.F("RefreshUseLink", waiting.FileName)))
            {
                _ = ApplyRefreshAsync(waiting, r);
                return;
            }
        }

        // a video page (YouTube, Vimeo, …): pick a quality
        if (r.Site || SiteVideo.IsSiteVideoUrl(r.Url))
        {
            var v = new VideoWindow(_m, r.Url, r.Referrer, r.Cookies, r.UserAgent, r.Title) { Topmost = true };
            v.Show();
            v.Activate();
            return;
        }

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

    async Task ApplyRefreshAsync(DownloadItem item, BridgeRequest r)
    {
        var error = await _m.RefreshAddressAsync(item, r.Url, r.Referrer, r.Cookies);
        if (error != null) Dialogs.Info(null, error, warning: true);
        else ShowBalloon(Loc.T("RefreshTitle"), Loc.F("RefreshDone", item.FileName));
    }

    /// <summary>"Download all links with SwiftGet" from the browser.</summary>
    public void HandleBrowserLinks(BridgeLinks r)
    {
        var links = r.Items.Select(i => new LinkCandidate(i.Url, r.Referrer, i.Cookies, string.IsNullOrWhiteSpace(i.FileName) ? null : i.FileName));
        var w = new LinksWindow(_m, links, Loc.T("LinksFromPage"), r.Title) { Topmost = true };
        w.Show();
        w.Activate();
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
    // ------------------------------------------------------------------ start / stop queue lists

    /// <summary>With one queue the button acts directly; with several it shows the list to choose from.</summary>
    void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_m.Queues.Count == 1) { _m.StartQueue(_m.MainQueue); return; }
        ShowQueueMenu((FrameworkElement)sender, BuildQueueItems(start: true));
    }

    void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        var running = _m.Queues.Where(q => q.IsRunning).ToList();
        if (running.Count == 1) { _m.StopQueue(running[0]); return; }
        ShowQueueMenu((FrameworkElement)sender, BuildQueueItems(start: false));
    }

    void ShowQueueMenu(FrameworkElement anchor, List<object> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FlowDirection = Loc.I.Flow };
        foreach (var i in items) menu.Items.Add(i);
        menu.IsOpen = true;
    }

    void QueueMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parent || e.OriginalSource != parent) return;
        parent.Items.Clear();
        foreach (var i in BuildQueueItems(start: parent == StartQueueMenu)) parent.Items.Add(i);
    }

    /// <summary>One entry per queue (with its number of files), plus "all queues".</summary>
    List<object> BuildQueueItems(bool start)
    {
        var items = new List<object>();
        foreach (var q in _m.Queues)
        {
            var queue = q;
            int files = _m.ItemsOf(q).Count(i => i.Status != DownloadStatus.Completed);
            var mi = new MenuItem
            {
                Header = $"{q.DisplayName}   ({Loc.F("QFilesCount", files)})",
                IsEnabled = start ? !q.IsRunning : q.IsRunning,
                IsChecked = q.IsRunning,
            };
            mi.Click += (_, _) => { if (start) _m.StartQueue(queue); else _m.StopQueue(queue); UpdateStatus(); };
            items.Add(mi);
        }
        items.Add(new Separator());
        var all = new MenuItem { Header = Loc.T(start ? "StartAllQueues" : "StopAllQueues") };
        all.IsEnabled = start ? _m.Queues.Any(q => !q.IsRunning) : _m.Queues.Any(q => q.IsRunning);
        all.Click += (_, _) =>
        {
            foreach (var q in _m.Queues.ToList())
                if (start && !q.IsRunning) _m.StartQueue(q);
                else if (!start && q.IsRunning) _m.StopQueue(q);
            UpdateStatus();
        };
        items.Add(all);
        return items;
    }
    void Schedule_Click(object sender, RoutedEventArgs e) => OpenScheduler(_selectedNav?.Queue);

    QueuesWindow? _scheduler;

    public void OpenScheduler(DownloadQueue? queue = null)
    {
        if (_scheduler != null) { _scheduler.Select(queue); _scheduler.Activate(); return; }
        _scheduler = new QueuesWindow(_m, queue) { Owner = IsVisible ? this : null };
        _scheduler.Closed += (_, _) => { _scheduler = null; UpdateStatus(); };
        _scheduler.Show();
    }
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

    /// <summary>Fills the "Add to queue" submenu with the current queues.</summary>
    void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        AddToQueueMenu.Items.Clear();
        foreach (var q in _m.Queues)
        {
            var queue = q;
            var mi = new MenuItem { Header = queue.DisplayName };
            mi.Click += (_, _) =>
            {
                foreach (var i in Selected().Where(i => i.Status != DownloadStatus.Completed)) _m.SetQueue(i, queue);
                UpdateStatus();
            };
            AddToQueueMenu.Items.Add(mi);
        }
        AddToQueueMenu.Items.Add(new Separator());
        var manage = new MenuItem { Header = Loc.T("Scheduler") + "…" };
        manage.Click += (_, _) => OpenScheduler();
        AddToQueueMenu.Items.Add(manage);
    }

    void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected()) _m.SetQueue(i, null);
        UpdateStatus();
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
        else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) { Batch_Click(this, e); e.Handled = true; }
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
        // a notification can carry an action (e.g. "update available" opens the update window)
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonAction; _balloonAction = null; a?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonAction = null;
        RebuildTrayMenu();
    }

    void RebuildTrayMenu()
    {
        if (_tray == null) return;
        var menu = new WinForms.ContextMenuStrip
        {
            RightToLeft = Loc.I.IsRtl ? WinForms.RightToLeft.Yes : WinForms.RightToLeft.No
        };
        if (_available is { } update && update.Version.ToString() != _m.Settings.SkippedVersion)
        {
            var item = menu.Items.Add(Loc.F("UpdTrayItem", update.Version), null, (_, _) => ShowUpdateWindow(update));
            item.Font = new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }
        menu.Items.Add(Loc.T("TrayShow"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Loc.T("TrayAdd"), null, (_, _) => { ShowFromTray(); AddUrl_Click(this, new RoutedEventArgs()); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("StartQueue"), null, (_, _) => _m.StartQueue(_m.MainQueue));
        menu.Items.Add(Loc.T("StopQueue"), null, (_, _) => _m.StopQueue(_m.MainQueue));
        menu.Items.Add(Loc.T("Scheduler") + "…", null, (_, _) => { ShowFromTray(); OpenScheduler(); });
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

    Action? _balloonAction;

    void ShowBalloon(string title, string text) => ShowBalloon(title, text, null);

    void ShowBalloon(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        _tray?.ShowBalloonTip(6000, title, string.IsNullOrEmpty(text) ? " " : text, WinForms.ToolTipIcon.Info);
    }

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
