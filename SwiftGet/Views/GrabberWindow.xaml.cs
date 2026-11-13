using System.Windows;
using SwiftGet.Core;

namespace SwiftGet.Views;

/// <summary>Site grabber: explore a site and pick the files found.</summary>
public partial class GrabberWindow : Window
{
    readonly DownloadManager _m;
    CancellationTokenSource? _cts;

    public GrabberWindow(DownloadManager manager, string? startUrl = null)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        DepthBox.ItemsSource = new[] { 0, 1, 2, 3, 4, 5 };
        DepthBox.SelectedItem = 1;
        PagesBox.ItemsSource = new[] { 20, 50, 100, 200, 500, 1000 };
        PagesBox.SelectedItem = 100;
        UrlBox.Text = startUrl ?? "https://";
        Closed += (_, _) => _cts?.Cancel();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var start) || (start.Scheme != "http" && start.Scheme != "https"))
        {
            StatusText.Text = Loc.T("ProbeInvalidUrl");
            return;
        }
        var types = TypesBox.Text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimStart('.').ToLowerInvariant()).ToHashSet();
        var grabber = new SiteGrabber
        {
            Start = start,
            Depth = DepthBox.SelectedItem is int d ? d : 1,
            MaxPages = PagesBox.SelectedItem is int p ? p : 100,
            SameSite = SameSiteBox.IsChecked == true,
            Extensions = types,
        };
        grabber.Progress += (pages, files) => Dispatcher.BeginInvoke(() => StatusText.Text = Loc.F("GrabProgress", pages, files));

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = Loc.T("GrabWorking");
        try
        {
            var files = await grabber.RunAsync(_cts.Token);
            if (files.Count == 0)
            {
                StatusText.Text = Loc.F("GrabNothing", grabber.PagesVisited);
                return;
            }
            new LinksWindow(_m, files.Select(f => new LinkCandidate(f, start.ToString())), Loc.T("GrabTitle"),
                Loc.F("GrabFound", files.Count, grabber.PagesVisited)) { Owner = Owner }.Show();
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            if (IsLoaded)
            {
                StartButton.IsEnabled = true;
                Busy.Visibility = Visibility.Collapsed;
            }
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested && !StartButton.IsEnabled) _cts.Cancel();
        else Close();
    }
}
