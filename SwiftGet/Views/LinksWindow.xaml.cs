using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SwiftGet.Core;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>A link offered for download (from a page, a list file, a batch pattern or the site grabber).</summary>
public sealed record LinkCandidate(string Url, string? Referrer = null, string? Cookies = null, string? FileName = null);

/// <summary>Pick which of many links to download.</summary>
public partial class LinksWindow : Window
{
    public sealed class Row : INotifyPropertyChanged
    {
        bool _selected = true;
        public required LinkCandidate Link { get; init; }
        public required string FileName { get; init; }
        public string Url => Link.Url;
        public required CategoryInfo Category { get; init; }
        public string Glyph => Category.Glyph;
        public Brush Brush => Category.Brush;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    readonly DownloadManager _m;
    readonly List<Row> _rows;
    string? _category;
    bool _folderEdited;

    public LinksWindow(DownloadManager manager, IEnumerable<LinkCandidate> links, string title, string? subtitle = null)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        Title = HeaderText.Text = title;
        SubText.Text = subtitle ?? "";

        _rows = links
            .Where(l => Uri.TryCreate(l.Url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "ftp"))
            .GroupBy(l => l.Url).Select(g => g.First())
            .Select(l =>
            {
                var name = Format.SanitizeFileName(string.IsNullOrWhiteSpace(l.FileName) ? Format.FileNameFromUrl(l.Url) : l.FileName);
                return new Row { Link = l, FileName = name, Category = Categories.Get(Categories.Detect(name)) };
            })
            .ToList();

        // pre-select real files; leave plain pages unticked
        foreach (var r in _rows) r.Selected = r.Category.Key != "Other" || Path.HasExtension(r.FileName);

        BuildChips();
        QueueBox.ItemsSource = _m.Queues;
        QueueBox.SelectedItem = _m.MainQueue;
        FolderBox.Text = _m.Settings.DefaultFolder;
        FolderBox.TextChanged += (_, _) => _folderEdited = true;
        _folderEdited = false;
        Refresh();
    }

    void BuildChips()
    {
        void Chip(string? key, string text)
        {
            var b = new RadioButton
            {
                Content = text, GroupName = "type", Margin = new Thickness(0, 0, 14, 0),
                IsChecked = key == null, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center,
            };
            b.Checked += (_, _) => { _category = key; Refresh(); };
            TypeChips.Children.Add(b);
        }
        Chip(null, Loc.F("LinksAll", _rows.Count));
        foreach (var c in Categories.All)
        {
            int n = _rows.Count(r => r.Category == c);
            if (n > 0) Chip(c.Key, $"{c.Title} ({n})");
        }
    }

    IEnumerable<Row> Visible()
    {
        var filter = FilterBox.Text.Trim();
        return _rows.Where(r => (_category == null || r.Category.Key == _category) &&
                                (filter.Length == 0 || Matches(r, filter)));
    }

    /// <summary>Plain text, or a wildcard like *.mp4.</summary>
    static bool Matches(Row r, string filter)
    {
        if (filter.Contains('*') || filter.Contains('?'))
        {
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(r.FileName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return r.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Url.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    void Refresh()
    {
        var visible = Visible().ToList();
        List.ItemsSource = visible;
        AllBox.IsChecked = visible.Count > 0 && visible.All(r => r.Selected) ? true : visible.Any(r => r.Selected) ? null : false;
        CountText.Text = Loc.F("LinksSelected", _rows.Count(r => r.Selected), _rows.Count);
    }

    void Filter_Changed(object sender, TextChangedEventArgs e) => Refresh();

    void All_Click(object sender, RoutedEventArgs e)
    {
        bool on = AllBox.IsChecked == true;
        foreach (var r in Visible()) r.Selected = on;
        Refresh();
    }

    void Row_Click(object sender, RoutedEventArgs e) =>
        CountText.Text = Loc.F("LinksSelected", _rows.Count(r => r.Selected), _rows.Count);

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("FolderPickerTitle") };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) { FolderBox.Text = dlg.FolderName; _folderEdited = true; }
    }

    void Start_Click(object sender, RoutedEventArgs e) => Submit(start: true);
    void Later_Click(object sender, RoutedEventArgs e) => Submit(start: false);

    void Submit(bool start)
    {
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0) return;
        var queue = QueueBox.SelectedItem as DownloadQueue ?? _m.MainQueue;
        foreach (var r in chosen)
        {
            var item = _m.CreateItem(r.Url, r.FileName, r.Link.Referrer, r.Link.Cookies);
            item.AutoFileName = r.Link.FileName == null;   // a better name may come from the server
            if (_folderEdited) item.SaveFolder = FolderBox.Text.Trim();
            item.QueueId = queue.Id;
            item.Status = DownloadStatus.Queued;
            _m.Add(item);
        }
        // "Start" runs the queue; with many files that respects the queue's parallel limit
        if (start) _m.StartQueue(queue);
        Close();
    }
}
