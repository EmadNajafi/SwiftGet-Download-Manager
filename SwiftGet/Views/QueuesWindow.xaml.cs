using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>Scheduler: manage queues, their schedule, finish action and file order.</summary>
public partial class QueuesWindow : Window
{
    public sealed record FileRow(int Position, DownloadItem Item);
    sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    static readonly (string Key, DayOfWeek Day)[] PersianWeek =
    {
        ("DaySat", DayOfWeek.Saturday), ("DaySun", DayOfWeek.Sunday), ("DayMon", DayOfWeek.Monday), ("DayTue", DayOfWeek.Tuesday),
        ("DayWed", DayOfWeek.Wednesday), ("DayThu", DayOfWeek.Thursday), ("DayFri", DayOfWeek.Friday),
    };
    static readonly (string Key, DayOfWeek Day)[] EnglishWeek =
    {
        ("DayMon", DayOfWeek.Monday), ("DayTue", DayOfWeek.Tuesday), ("DayWed", DayOfWeek.Wednesday), ("DayThu", DayOfWeek.Thursday),
        ("DayFri", DayOfWeek.Friday), ("DaySat", DayOfWeek.Saturday), ("DaySun", DayOfWeek.Sunday),
    };

    readonly DownloadManager _m;
    readonly CheckBox[] _days = new CheckBox[7];
    readonly DispatcherTimer _timer;
    DownloadQueue? _current;
    bool _loading;

    public QueuesWindow(DownloadManager manager, DownloadQueue? select = null)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;

        ConcurrentBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        SyncHoursBox.ItemsSource = new[] { 1, 2, 4, 6, 12, 24, 48, 168 }.Select(h => new Choice<int>(h, Loc.F("QSyncHours", h))).ToArray();
        ActionBox.ItemsSource = new[]
        {
            new Choice<QueueFinishAction>(QueueFinishAction.None, Loc.T("ActNone")),
            new Choice<QueueFinishAction>(QueueFinishAction.ExitApp, Loc.T("ActExit")),
            new Choice<QueueFinishAction>(QueueFinishAction.Sleep, Loc.T("ActSleep")),
            new Choice<QueueFinishAction>(QueueFinishAction.Hibernate, Loc.T("ActHibernate")),
            new Choice<QueueFinishAction>(QueueFinishAction.Shutdown, Loc.T("ActShutdown")),
        };
        foreach (var (key, day) in Loc.I.IsRtl ? PersianWeek : EnglishWeek)
        {
            var box = new CheckBox { Content = Loc.T(key), MinWidth = 84, Margin = new Thickness(0, 0, 6, 6), Foreground = (Brush)FindResource("Text") };
            box.Click += Editor_Changed;
            _days[(int)day] = box;
            DaysPanel.Children.Add(box);
        }

        QueueList.ItemsSource = _m.Queues;
        QueueList.SelectedItem = select ?? _m.MainQueue;

        _m.QueueStateChanged += UpdateState;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { UpdateState(); RefreshFiles(keepSelection: true); };
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _m.QueueStateChanged -= UpdateState;
        };
    }

    public void Select(DownloadQueue? queue)
    {
        if (queue != null) QueueList.SelectedItem = queue;
    }

    // ------------------------------------------------------------------ load / save editor

    void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveEditor();
        _current = QueueList.SelectedItem as DownloadQueue;
        LoadEditor();
    }

    void LoadEditor()
    {
        if (_current is not { } q) return;
        _loading = true;
        NameBox.Text = q.DisplayName;
        NameBox.IsEnabled = !q.IsMain;
        DeleteQueueButton.IsEnabled = !q.IsMain;
        StartOnLaunchBox.IsChecked = q.StartOnLaunch;
        StartEnabledBox.IsChecked = q.StartEnabled;
        StartTimeBox.Text = q.StartTime;
        OnceRadio.IsChecked = q.StartMode == QueueStartMode.Once;
        DailyRadio.IsChecked = q.StartMode == QueueStartMode.Daily;
        OnceDate.SelectedDate = q.OnceDate < DateTime.Today ? DateTime.Today : q.OnceDate;
        for (int d = 0; d < 7; d++) _days[d].IsChecked = q.Days.Length == 7 && q.Days[d];
        StopEnabledBox.IsChecked = q.StopEnabled;
        StopTimeBox.Text = q.StopTime;
        ConcurrentBox.SelectedItem = Math.Clamp(q.MaxConcurrent, 1, 10);
        ActionBox.SelectedItem = ((Choice<QueueFinishAction>[])ActionBox.ItemsSource).First(c => c.Value == q.FinishAction);
        SyncBox.IsChecked = q.SyncEnabled;
        var hours = (Choice<int>[])SyncHoursBox.ItemsSource;
        SyncHoursBox.SelectedItem = hours.FirstOrDefault(h => h.Value == q.SyncIntervalHours) ?? hours[5];
        _loading = false;
        UpdateEnabled();
        UpdateState();
        RefreshFiles(keepSelection: false);
    }

    void SaveEditor()
    {
        if (_current is not { } q || _loading) return;
        if (!q.IsMain && !string.IsNullOrWhiteSpace(NameBox.Text)) q.Name = NameBox.Text.Trim();
        q.StartOnLaunch = StartOnLaunchBox.IsChecked == true;
        q.StartEnabled = StartEnabledBox.IsChecked == true;
        if (IsTime(StartTimeBox.Text)) q.StartTime = Normalize(StartTimeBox.Text);
        q.StartMode = OnceRadio.IsChecked == true ? QueueStartMode.Once : QueueStartMode.Daily;
        q.OnceDate = OnceDate.SelectedDate ?? DateTime.Today;
        q.Days = _days.Select(b => b.IsChecked == true).ToArray();
        q.StopEnabled = StopEnabledBox.IsChecked == true;
        if (IsTime(StopTimeBox.Text)) q.StopTime = Normalize(StopTimeBox.Text);
        if (ConcurrentBox.SelectedItem is int n) q.MaxConcurrent = n;
        if (ActionBox.SelectedItem is Choice<QueueFinishAction> a) q.FinishAction = a.Value;
        q.SyncEnabled = SyncBox.IsChecked == true;
        if (SyncHoursBox.SelectedItem is Choice<int> h) q.SyncIntervalHours = h.Value;
        // a changed schedule should fire again today
        q.LastStartFire = q.LastStopFire = null;
        _m.QueueSettingsChanged();
    }

    void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender == StartTimeBox || sender == StopTimeBox)
        {
            var box = (TextBox)sender;
            if (!IsTime(box.Text))
            {
                box.Text = box == StartTimeBox ? _current?.StartTime : _current?.StopTime;
                return;
            }
            box.Text = Normalize(box.Text);
        }
        SaveEditor();
        UpdateEnabled();
    }

    void UpdateEnabled()
    {
        bool start = StartEnabledBox.IsChecked == true;
        StartTimeBox.IsEnabled = start;
        StartOptions.IsEnabled = start;
        OnceDate.IsEnabled = OnceRadio.IsChecked == true;
        DaysPanel.IsEnabled = DailyRadio.IsChecked == true;
        StopTimeBox.IsEnabled = StopEnabledBox.IsChecked == true;
        SyncHoursBox.IsEnabled = SyncBox.IsChecked == true;
    }

    void UpdateState()
    {
        if (_current is not { } q) return;
        StateText.Text = q.StateText;
        StatePill.Background = (Brush)FindResource(q.IsRunning ? "SuccessSoft" : "NeutralSoft");
        StateText.Foreground = (Brush)FindResource(q.IsRunning ? "Success" : "TextSecondary");
        StartButton.IsEnabled = !q.IsRunning;
        StopButton.IsEnabled = q.IsRunning;
        FilesTab.Header = $"{Loc.T("TabFiles")} ({_m.ItemsOf(q).Count})";
    }

    void RefreshFiles(bool keepSelection)
    {
        if (_current is not { } q) return;
        var selected = FileList.SelectedItems.Cast<FileRow>().Select(r => r.Item.Id).ToHashSet();
        var rows = _m.ItemsOf(q).Select((item, i) => new FileRow(i + 1, item)).ToList();
        if (keepSelection && FileList.ItemsSource is List<FileRow> old &&
            old.Select(r => (r.Position, r.Item.Id)).SequenceEqual(rows.Select(r => (r.Position, r.Item.Id))))
            return;   // nothing moved; avoid resetting the list
        FileList.ItemsSource = rows;
        foreach (var r in rows.Where(r => selected.Contains(r.Item.Id))) FileList.SelectedItems.Add(r);
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ commands

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor();
        int n = 1;
        while (_m.Queues.Any(q => q.Name == Loc.F("NewQueueName", n))) n++;
        var queue = _m.CreateQueue(Loc.F("NewQueueName", n));
        QueueList.SelectedItem = queue;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void DeleteQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsMain: false } q) return;
        if (!Dialogs.Ask(this, Loc.F("ConfirmDeleteQueue", q.DisplayName))) return;
        _current = null;
        _m.DeleteQueue(q);
        QueueList.SelectedItem = _m.MainQueue;
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor();
        if (_current != null) _m.StartQueue(_current);
        UpdateState();
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _m.StopQueue(_current);
        UpdateState();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    void MoveSelected(int direction)
    {
        var items = FileList.SelectedItems.Cast<FileRow>().OrderBy(r => r.Position).Select(r => r.Item).ToList();
        if (items.Count == 0) return;
        if (direction > 0) items.Reverse();
        foreach (var item in items) _m.Move(item, direction);
        RefreshFiles(keepSelection: false);
        foreach (var r in FileList.Items.Cast<FileRow>().Where(r => items.Contains(r.Item))) FileList.SelectedItems.Add(r);
    }

    void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in FileList.SelectedItems.Cast<FileRow>().ToList()) _m.SetQueue(r.Item, null);
        RefreshFiles(keepSelection: false);
        UpdateState();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveEditor();
        base.OnClosing(e);
    }

    static bool IsTime(string? text) =>
        TimeSpan.TryParse(text?.Trim(), out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);

    static string Normalize(string text) => TimeSpan.Parse(text.Trim()).ToString(@"hh\:mm");
}
