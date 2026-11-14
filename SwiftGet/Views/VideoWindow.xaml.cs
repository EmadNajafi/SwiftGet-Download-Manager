using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>Download a video from a site like YouTube: shows the video and lets the user pick a quality.</summary>
public partial class VideoWindow : Window
{
    readonly DownloadManager _m;
    readonly string _url;
    readonly string? _referrer, _cookies, _userAgent;
    readonly CancellationTokenSource _cts = new();
    VideoInfo? _info;
    string _autoName = "";

    public VideoWindow(DownloadManager manager, string url, string? referrer = null, string? cookies = null, string? userAgent = null, string? title = null)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        _url = url;
        _referrer = referrer;
        _cookies = cookies;
        _userAgent = userAgent;
        SiteText.Text = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? url : title;
        QueueBox.ItemsSource = _m.Queues;
        QueueBox.SelectedItem = _m.MainQueue;
        FolderBox.Text = Categories.FolderFor(_m.Settings, "Video");
        Loaded += async (_, _) => await BeginAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    async Task BeginAsync()
    {
        long missing = SiteVideo.MissingToolsSize(_m.Settings);
        if (missing > 0)
        {
            ShowStatus(Loc.F("VideoToolsNeeded", Format.Size(missing)), busy: false, tools: true);
            return;
        }
        await LoadInfoAsync();
    }

    void ShowStatus(string text, bool busy, bool tools = false, bool retry = false, bool error = false)
    {
        StatusCard.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.IsIndeterminate = true;
        StatusButtons.Visibility = tools || retry ? Visibility.Visible : Visibility.Collapsed;
        ToolsButton.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        (StatusGlyph.Text, StatusGlyph.Foreground) = error
            ? ("", (Brush)FindResource("Warning"))
            : ("", (Brush)FindResource("Accent"));
    }

    async void GetTools_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(Loc.F("VideoToolsProgress", 0), busy: true);
        StatusBar.IsIndeterminate = false;
        var progress = new Progress<double>(p =>
        {
            StatusBar.Value = p * 100;
            StatusText.Text = Loc.F("VideoToolsProgress", (int)(p * 100));
        });
        try
        {
            await SiteVideo.EnsureToolsAsync(_m.Settings, progress, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            ShowStatus(Loc.F("VideoToolsFailed", ex.Message), busy: false, tools: true, error: true);
            return;
        }
        await LoadInfoAsync();
    }

    async void Retry_Click(object sender, RoutedEventArgs e) => await BeginAsync();

    async Task LoadInfoAsync()
    {
        ShowStatus(Loc.T("VideoLoading"), busy: true);
        try
        {
            // keeps yt-dlp current first (sites change often); quick when it is
            await SiteVideo.EnsureToolsAsync(_m.Settings, null, _cts.Token);
            _info = await SiteVideo.GetInfoAsync(_m.Settings, _url, _cookies, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            ShowStatus(Loc.F("VideoError", ex.Message), busy: false, retry: true, error: true);
            return;
        }

        TitleText.Text = _info.Title;
        DurationText.Text = _info.Duration > 0 ? Format.Eta(TimeSpan.FromSeconds(_info.Duration)) : "";
        if (_info.Thumbnail is { } thumb && Uri.TryCreate(thumb, UriKind.Absolute, out var t))
        {
            try { Thumb.Source = new BitmapImage(t); } catch { }
        }
        StatusCard.Visibility = Visibility.Collapsed;
        ChoicePanel.Visibility = Visibility.Visible;
        ChoiceList.ItemsSource = _info.Choices;
        // default: 1080p when offered (good quality, reasonable size), otherwise the best below it
        ChoiceList.SelectedItem = _info.Choices.FirstOrDefault(c => !c.AudioOnly && Height(c) <= 1080 && Height(c) > 0) ?? _info.Choices[0];
        StartButton.IsEnabled = LaterButton.IsEnabled = true;
    }

    static int Height(VideoChoice c) => int.TryParse(new string(c.Label.TakeWhile(char.IsDigit).ToArray()), out var h) ? h : 0;

    void ChoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_info == null || ChoiceList.SelectedItem is not VideoChoice c) return;
        if (NameBox.Text.Length > 0 && NameBox.Text != _autoName) return;   // the user typed a name
        var stem = Format.SanitizeFileName(_info.Title);
        if (stem.Length > 120) stem = stem[..120];
        _autoName = $"{stem}.{c.Ext}";
        NameBox.Text = _autoName;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("FolderPickerTitle") };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    void Start_Click(object sender, RoutedEventArgs e) => Submit(startNow: true);
    void Later_Click(object sender, RoutedEventArgs e) => Submit(startNow: false);

    void Submit(bool startNow)
    {
        if (_info == null || ChoiceList.SelectedItem is not VideoChoice c) return;
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
        {
            Dialogs.Info(this, Loc.T("ErrNoFolder"), warning: true);
            return;
        }
        var name = Format.SanitizeFileName(string.IsNullOrWhiteSpace(NameBox.Text) ? _autoName : NameBox.Text.Trim());
        if (!name.EndsWith("." + c.Ext, StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name) + "." + c.Ext;

        var item = new DownloadItem
        {
            Url = _info.PageUrl,
            FileName = name,
            SaveFolder = folder,
            Category = c.AudioOnly ? "Music" : "Video",
            Referrer = _referrer,
            Cookies = _cookies,
            UserAgent = _userAgent,
            SiteFormat = c.Format,
            TotalSize = c.Size > 0 ? c.Size : -1,
            ResumeSupported = true,
        };
        if (startNow)
        {
            _m.Add(item);
            _m.StartWithProgress(item);
        }
        else
        {
            item.QueueId = (QueueBox.SelectedItem as DownloadQueue ?? _m.MainQueue).Id;
            item.Status = DownloadStatus.Queued;
            _m.Add(item);
        }
        Close();
    }
}
