using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

public partial class AddDownloadWindow : Window
{
    readonly DownloadManager _m;
    readonly string? _referrer, _cookies, _userAgent, _nameHint;
    CancellationTokenSource? _probeCts;
    string _probedUrl = "";
    string _autoName = "", _autoFolder = "";
    bool _settingCategory;
    long _size = -1;
    bool _resume;

    public AddDownloadWindow(DownloadManager manager, string? url,
        string? referrer = null, string? cookies = null, string? userAgent = null, string? fileName = null)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        _referrer = referrer;
        _cookies = cookies;
        _userAgent = userAgent;
        _nameHint = string.IsNullOrWhiteSpace(fileName) ? null : Format.SanitizeFileName(fileName);

        CategoryBox.ItemsSource = Categories.All;
        QueueBox.ItemsSource = _m.Queues;
        QueueBox.SelectedItem = _m.MainQueue;
        UrlBox.Text = url ?? "";
        if (_nameHint != null) SetAutoName(_nameHint);
        else if (!string.IsNullOrEmpty(url)) SetAutoName(Format.FileNameFromUrl(url));
        else SelectCategory("Other");
        SetInfo(Loc.T("ProbeEnterUrl"), InfoKind.Neutral);

        Loaded += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(UrlBox.Text)) UrlBox.Focus();
            else await ProbeAsync();
        };
        Closed += (_, _) => _probeCts?.Cancel();
    }

    enum InfoKind { Neutral, Busy, Ok, Error }

    void SetInfo(string text, InfoKind kind)
    {
        InfoText.Text = text;
        (InfoGlyph.Text, InfoGlyph.Foreground) = kind switch
        {
            InfoKind.Ok => ("", (Brush)FindResource("Success")),
            InfoKind.Error => ("", (Brush)FindResource("Warning")),
            InfoKind.Busy => ("", (Brush)FindResource("Accent")),
            _ => ("", (Brush)FindResource("TextSecondary")),
        };
    }

    // ------------------------------------------------------------------ probe

    async void Probe_Click(object sender, RoutedEventArgs e) => await ProbeAsync();

    async void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (UrlBox.Text.Trim() != _probedUrl) await ProbeAsync();
    }

    async Task ProbeAsync()
    {
        var url = UrlBox.Text.Trim();
        if (!IsValidUrl(url))
        {
            SetInfo(Loc.T("ProbeInvalidUrl"), InfoKind.Error);
            return;
        }

        if (SiteVideo.IsSiteVideoUrl(url))
        {
            var v = new VideoWindow(_m, url, _referrer, _cookies, _userAgent) { Topmost = Topmost };
            v.Show();
            Close();
            return;
        }

        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        _probedUrl = url;
        SetInfo(Loc.T("ProbeChecking"), InfoKind.Busy);
        SizeValue.Text = ResumeValue.Text = TypeValue.Text = "…";
        ResumeGlyph.Text = "";
        try
        {
            var r = await DownloadEngine.ProbeAsync(url, _referrer, _cookies, _userAgent, cts.Token);
            if (cts.IsCancellationRequested) return;
            _size = r.Size;
            _resume = r.ResumeSupported;
            SetAutoName(_nameHint ?? r.FileName);
            SizeValue.Text = r.Size > 0 ? Format.Size(r.Size) : Loc.T("Unknown");
            ResumeValue.Text = Loc.T(r.ResumeSupported ? "ResumeYes" : "ResumeNo");
            ResumeGlyph.Text = r.ResumeSupported ? "" : "";
            ResumeGlyph.Foreground = (Brush)FindResource(r.ResumeSupported ? "Success" : "Warning");
            TypeValue.Text = r.ContentType ?? "—";
            SetInfo(Loc.T("ProbeOk"), InfoKind.Ok);
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) SetAutoName(Format.FileNameFromUrl(url));
            SizeValue.Text = ResumeValue.Text = TypeValue.Text = "—";
            SetInfo(Loc.F("ProbeFailed", ex.Message), InfoKind.Error);
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ name / category / folder

    /// <summary>Fills the name only if the user has not typed their own.</summary>
    void SetAutoName(string name)
    {
        if (NameBox.Text.Length > 0 && NameBox.Text != _autoName) return;
        _autoName = name;
        NameBox.Text = name;
    }

    void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NameBox.Text.Length > 0) SelectCategory(Categories.Detect(NameBox.Text));
    }

    void SelectCategory(string key)
    {
        _settingCategory = true;
        CategoryBox.SelectedItem = Categories.Get(key);
        _settingCategory = false;
        UpdateAutoFolder();
    }

    void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingCategory) UpdateAutoFolder();
    }

    void UpdateAutoFolder()
    {
        if (CategoryBox.SelectedItem is not CategoryInfo c) return;
        if (FolderBox.Text.Length > 0 && FolderBox.Text != _autoFolder) return;
        _autoFolder = Categories.FolderFor(_m.Settings, c.Key);
        FolderBox.Text = _autoFolder;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("FolderPickerTitle") };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    // ------------------------------------------------------------------ actions

    void Start_Click(object sender, RoutedEventArgs e) => Submit(startNow: true);
    void Later_Click(object sender, RoutedEventArgs e) => Submit(startNow: false);

    void Submit(bool startNow)
    {
        var url = UrlBox.Text.Trim();
        if (!IsValidUrl(url))
        {
            SetInfo(Loc.T("ProbeInvalidUrl"), InfoKind.Error);
            return;
        }
        var name = Format.SanitizeFileName(string.IsNullOrWhiteSpace(NameBox.Text) ? Format.FileNameFromUrl(url) : NameBox.Text);
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
        {
            SetInfo(Loc.T("ErrNoFolder"), InfoKind.Error);
            return;
        }

        var item = new DownloadItem
        {
            Url = url,
            FileName = name,
            AutoFileName = false,
            SaveFolder = folder,
            Category = (CategoryBox.SelectedItem as CategoryInfo)?.Key ?? Categories.Detect(name),
            Referrer = _referrer,
            Cookies = _cookies,
            UserAgent = _userAgent,
            ResumeSupported = _resume,
        };
        if (url == _probedUrl) item.TotalSize = _size;

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

    static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
