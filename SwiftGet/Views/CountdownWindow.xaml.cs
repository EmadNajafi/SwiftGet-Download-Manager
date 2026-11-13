using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SwiftGet.Core;
using SwiftGet.Models;
using WinForms = System.Windows.Forms;

namespace SwiftGet.Views;

/// <summary>Counts down before a queue's finish action (shut down, sleep…), so the user can cancel it.</summary>
public partial class CountdownWindow : Window
{
    const int Seconds = 30;

    readonly QueueFinishAction _action;
    readonly DispatcherTimer _timer;
    int _left = Seconds;
    bool _done;

    public CountdownWindow(DownloadQueue queue, QueueFinishAction action)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _action = action;
        QueueText.Text = Loc.F("NotifyQueueDoneNamed", queue.DisplayName);
        Glyph.Text = action switch
        {
            QueueFinishAction.Sleep or QueueFinishAction.Hibernate => "",
            QueueFinishAction.ExitApp => "",
            _ => "",
        };
        Bar.Maximum = Seconds;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        UpdateText();
    }

    void UpdateText()
    {
        var key = _action switch
        {
            QueueFinishAction.Shutdown => "CountdownShutdown",
            QueueFinishAction.Sleep => "CountdownSleep",
            QueueFinishAction.Hibernate => "CountdownHibernate",
            _ => "CountdownExit",
        };
        MessageText.Text = Loc.F(key, _left);
        Bar.Value = Seconds - _left;
    }

    void Tick()
    {
        _left--;
        UpdateText();
        if (_left <= 0) Run();
    }

    void Now_Click(object sender, RoutedEventArgs e) => Run();
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Run()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Close();
        try
        {
            switch (_action)
            {
                case QueueFinishAction.Shutdown:
                    ((App)Application.Current).ExitApp();   // save state first
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    break;
                case QueueFinishAction.Sleep:
                    WinForms.Application.SetSuspendState(WinForms.PowerState.Suspend, false, false);
                    break;
                case QueueFinishAction.Hibernate:
                    WinForms.Application.SetSuspendState(WinForms.PowerState.Hibernate, false, false);
                    break;
                case QueueFinishAction.ExitApp:
                    ((App)Application.Current).ExitApp();
                    break;
            }
        }
        catch
        {
            // the action isn't available on this computer (e.g. hibernation is turned off)
        }
    }
}
