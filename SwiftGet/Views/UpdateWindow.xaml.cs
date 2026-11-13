using System.Windows;
using SwiftGet.Core;

namespace SwiftGet.Views;

public partial class UpdateWindow : Window
{
    readonly UpdateInfo _info;
    readonly DownloadManager _m;
    CancellationTokenSource? _cts;

    public UpdateWindow(DownloadManager manager, UpdateInfo info)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _m = manager;
        _info = info;
        HeadText.Text = Loc.F("UpdAvailable", info.Version);
        SubText.Text = Loc.F("UpdCurrent", Updater.Current);
        NotesBox.Text = string.IsNullOrWhiteSpace(info.Notes) ? info.PageUrl : info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        bool canInstall = Updater.IsInstalled && info.SetupUrl != null;
        InstallButton.Content = Loc.T(canInstall ? "UpdInstall" : "UpdOpenPage");
        Closed += (_, _) => _cts?.Cancel();
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!Updater.IsInstalled || _info.SetupUrl == null)
        {
            Shell.OpenUrl(_info.PageUrl);   // portable copy: let the user download it
            Close();
            return;
        }
        InstallButton.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        try
        {
            var path = await Updater.DownloadSetupAsync(_info, new Progress<double>(p => Bar.Value = p), _cts.Token);
            Updater.Install(path);
            ((App)Application.Current).ExitApp();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
        }
    }

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        _m.Settings.SkippedVersion = _info.Version.ToString();
        _m.ApplySettings(_m.Settings);
        Close();
    }
}
