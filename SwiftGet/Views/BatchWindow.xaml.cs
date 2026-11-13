using System.Windows;
using SwiftGet.Core;

namespace SwiftGet.Views;

/// <summary>Batch download: one address with a numbered/lettered pattern becomes many links.</summary>
public partial class BatchWindow : Window
{
    readonly DownloadManager _m;
    List<string> _links = new();

    public BatchWindow(DownloadManager manager)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        WidthBox.ItemsSource = new[] { "—", "2", "3", "4", "5" };
        WidthBox.SelectedIndex = 0;
        PatternBox.Text = "https://example.com/files/part[01-10].zip";
        Loaded += (_, _) => { PatternBox.Focus(); PatternBox.SelectAll(); };
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && sender != PatternBox) return;
        var pattern = PatternBox.Text.Trim();
        StarPanel.Visibility = pattern.Contains('*') ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            string? from = FromBox.Text, to = ToBox.Text;
            if (pattern.Contains('*') && WidthBox.SelectedIndex > 0 && long.TryParse(from, out var f))
                from = f.ToString().PadLeft(WidthBox.SelectedIndex + 1, '0');
            _links = BatchPattern.HasPattern(pattern) ? BatchPattern.Expand(pattern, from, to) : new();
            _links = _links.Where(l => Uri.TryCreate(l, UriKind.Absolute, out _)).ToList();
            CountText.Text = Loc.F("BatchCount", _links.Count);
            FirstText.Text = _links.FirstOrDefault() ?? "";
            LastText.Text = _links.Count > 1 ? "… " + _links[^1] : "";
        }
        catch (Exception ex)
        {
            _links = new();
            CountText.Text = ex.Message;
            FirstText.Text = LastText.Text = "";
        }
        NextButton.IsEnabled = _links.Count > 0;
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_links.Count == 0) return;
        new LinksWindow(_m, _links.Select(l => new LinkCandidate(l)), Loc.T("BatchTitle"), PatternBox.Text.Trim())
        {
            Owner = Owner,
        }.Show();
        Close();
    }
}
