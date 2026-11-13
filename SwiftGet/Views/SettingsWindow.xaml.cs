using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SwiftGet.Core;

namespace SwiftGet.Views;

public partial class SettingsWindow : Window
{
    sealed record Page(string Title, string Glyph, FrameworkElement Panel);
    sealed record Choice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    static readonly int[] ConnectionChoices = { 1, 2, 4, 8, 12, 16, 24, 32 };

    // Week starts on Saturday in Persian, Monday in English.
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

    readonly CheckBox[] _dayBoxes = new CheckBox[7];
    readonly AppSettings _current;
    readonly List<Page> _pages;

    public AppSettings? Result { get; private set; }

    static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    public SettingsWindow(AppSettings current, int page)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        AboutLogo.Source = AppIcon.LargeImageSource;
        _current = current;

        _pages = new()
        {
            new(Loc.T("SetGeneral"), "", PageGeneral),
            new(Loc.T("SetConnections"), "", PageConnections),
            new(Loc.T("SetBrowser"), "", PageBrowser),
            new(Loc.T("SetAppearance"), "", PageAppearance),
            new(Loc.T("SetAbout"), "", PageAbout),
        };
        PageList.ItemsSource = _pages;
        PageList.SelectedIndex = Math.Clamp(page, 0, _pages.Count - 1);

        // general
        FolderBox.Text = current.DefaultFolder;
        CategoryFoldersBox.IsChecked = current.UseCategoryFolders;
        StartupBox.IsChecked = Autostart.IsEnabled;
        ShowDialogBox.IsChecked = current.ShowDialogForBrowser;
        ShowCompleteBox.IsChecked = current.ShowCompleteDialog;
        ShowProgressBox.IsChecked = current.ShowProgressWindow;

        // connections
        ConnectionsBox.ItemsSource = ConnectionChoices;
        ConnectionsBox.SelectedItem = ConnectionChoices.Contains(current.Connections) ? current.Connections : 8;
        ConcurrentBox.Text = current.MaxConcurrent.ToString();
        RetriesBox.Text = current.MaxRetries.ToString();
        SpeedBox.Text = current.SpeedLimitKBps.ToString();
        PortBox.Text = current.BridgePort.ToString();

        // scheduler
        var s = current.Schedule;
        StartEnabledBox.IsChecked = s.StartEnabled;
        StartTimeBox.Text = s.StartTime;
        StopEnabledBox.IsChecked = s.StopEnabled;
        StopTimeBox.Text = s.StopTime;
        ShutdownBox.IsChecked = s.ShutdownWhenDone;
        foreach (var (key, day) in Loc.I.IsRtl ? PersianWeek : EnglishWeek)
        {
            var box = new CheckBox
            {
                Content = Loc.T(key),
                MinWidth = 86,
                Margin = new Thickness(0, 0, 8, 8),
                Foreground = (Brush)FindResource("Text"),
                IsChecked = s.Days.Length == 7 && s.Days[(int)day],
            };
            _dayBoxes[(int)day] = box;
            DaysPanel.Children.Add(box);
        }

        // browser
        ExtensionPathBox.Text = ExtensionFolder;
        int n = 1;
        foreach (var key in new[] { "BrowserStepA", "BrowserStepB", "BrowserStepC" })
            StepsPanel.Children.Add(Step(n++, Loc.T(key)));
        BuildBrowserButtons();
        UpdateExtensionStatus();
        var statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        statusTimer.Tick += (_, _) => UpdateExtensionStatus();
        statusTimer.Start();
        Closed += (_, _) => statusTimer.Stop();

        // appearance
        LanguageBox.ItemsSource = new[] { new Choice("", Loc.T("LangSystem")), new Choice("en", "English"), new Choice("fa", "فارسی") };
        LanguageBox.SelectedItem = ((Choice[])LanguageBox.ItemsSource).FirstOrDefault(c => c.Value == current.Language);
        ThemeBox.ItemsSource = new[]
        {
            new Choice("System", Loc.T("ThemeSystem")), new Choice("Light", Loc.T("ThemeLight")), new Choice("Dark", Loc.T("ThemeDark")),
        };
        ThemeBox.SelectedItem = ((Choice[])ThemeBox.ItemsSource).FirstOrDefault(c => c.Value == current.Theme) ?? ((Choice[])ThemeBox.ItemsSource)[0];

        VersionText.Text = Loc.F("AboutVersion", App.Version);
    }

    void BuildBrowserButtons()
    {
        var browsers = BrowserIntegration.Installed();
        ManualSteps.Visibility = browsers.Any(b => b.StoreUrl == null) || browsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (browsers.Count == 0)
        {
            BrowsersPanel.Children.Add(new TextBlock { Text = Loc.T("NoBrowsers"), Margin = new Thickness(10), Foreground = (Brush)FindResource("TextSecondary") });
            return;
        }
        foreach (var b in browsers)
        {
            var grid = new Grid { Margin = new Thickness(10, 6, 6, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = b.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Text") };
            var button = new Button
            {
                Content = Loc.F("AddToBrowser", b.Name),
                Style = (Style)FindResource(b == browsers[0] ? "AccentButton" : "SecondaryButton"),
                MinWidth = 170,
            };
            var browser = b;
            button.Click += (_, _) => BrowserIntegration.BeginInstall(browser);
            Grid.SetColumn(button, 1);
            grid.Children.Add(name);
            grid.Children.Add(button);
            BrowsersPanel.Children.Add(grid);
        }
    }

    void UpdateExtensionStatus()
    {
        var seen = BrowserBridge.LastExtensionContact;
        bool connected = seen != null && DateTime.Now - seen.Value < TimeSpan.FromMinutes(3);
        ExtStatusText.Text = Loc.T(connected ? "ExtConnected" : "ExtNotConnected");
        ExtStatusHint.Text = Loc.T(connected ? "ExtConnectedHint" : "ExtNotConnectedHint");
        ExtStatusGlyph.Text = connected ? "" : "";
        ExtStatusGlyph.Foreground = (Brush)FindResource(connected ? "Success" : "Warning");
        ExtStatusBadge.Background = (Brush)FindResource(connected ? "SuccessSoft" : "WarningSoft");
    }

    FrameworkElement Step(int number, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Background = (Brush)FindResource("AccentSoft"),
            Child = new TextBlock
            {
                Text = number.ToString(), FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(label, 1);
        grid.Children.Add(badge);
        grid.Children.Add(label);
        return grid;
    }

    void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages == null) return;
        foreach (var p in _pages)
            p.Panel.Visibility = ReferenceEquals(p, PageList.SelectedItem) ? Visibility.Visible : Visibility.Collapsed;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("FolderPickerTitle") };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    void OpenExtension_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(ExtensionFolder)) Process.Start("explorer.exe", $"\"{ExtensionFolder}\"");
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ExtensionFolder);
            CopyButton.Content = Loc.T("Copied");
        }
        catch { }
    }

    void Github_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(App.ProjectUrl);

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Path.IsPathRooted(FolderBox.Text.Trim())) { Fail(0, "ErrFolder"); return; }
        if (!int.TryParse(ConcurrentBox.Text, out var concurrent) || concurrent < 1 || concurrent > 20) { Fail(1, "ErrConcurrent"); return; }
        if (!int.TryParse(RetriesBox.Text, out var retries) || retries < 0 || retries > 100) { Fail(1, "ErrRetries"); return; }
        if (!int.TryParse(SpeedBox.Text, out var speed) || speed < 0) { Fail(1, "ErrSpeed"); return; }
        if (!int.TryParse(PortBox.Text, out var port) || port < 1024 || port > 65535) { Fail(1, "ErrPort"); return; }
        if (!IsTime(StartTimeBox.Text) || !IsTime(StopTimeBox.Text)) { Fail(2, "ErrTime"); return; }

        Result = new AppSettings
        {
            DefaultFolder = FolderBox.Text.Trim(),
            UseCategoryFolders = CategoryFoldersBox.IsChecked == true,
            StartWithWindows = StartupBox.IsChecked == true,
            ShowDialogForBrowser = ShowDialogBox.IsChecked == true,
            ShowCompleteDialog = ShowCompleteBox.IsChecked == true,
            ShowProgressWindow = ShowProgressBox.IsChecked == true,
            Connections = ConnectionsBox.SelectedItem is int c ? c : 8,
            MaxConcurrent = concurrent,
            MaxRetries = retries,
            SpeedLimitKBps = speed,
            BridgePort = port,
            Language = (LanguageBox.SelectedItem as Choice)?.Value ?? "",
            Theme = (ThemeBox.SelectedItem as Choice)?.Value ?? "System",
            Schedule = new ScheduleSettings
            {
                StartEnabled = StartEnabledBox.IsChecked == true,
                StartTime = StartTimeBox.Text.Trim(),
                StopEnabled = StopEnabledBox.IsChecked == true,
                StopTime = StopTimeBox.Text.Trim(),
                Days = _dayBoxes.Select(b => b.IsChecked == true).ToArray(),
                ShutdownWhenDone = ShutdownBox.IsChecked == true,
            },
        };

        if (port != _current.BridgePort) Dialogs.Info(this, Loc.T("PortRestart"));
        DialogResult = true;
    }

    void Fail(int page, string key)
    {
        PageList.SelectedIndex = page;
        Dialogs.Info(this, Loc.T(key), warning: true);
    }

    static bool IsTime(string text) =>
        TimeSpan.TryParse(text.Trim(), out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
}
