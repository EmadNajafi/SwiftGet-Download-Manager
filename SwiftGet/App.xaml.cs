using System.Reflection;
using System.Windows;
using SwiftGet.Core;
using SwiftGet.Views;

namespace SwiftGet;

public partial class App : Application
{
    public const string ProjectUrl = "https://github.com/EmadNajafi/SwiftGet-Download-Manager";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    DownloadManager? _manager;
    BrowserBridge? _bridge;
    MainWindow? _main;
    bool _exited;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "SwiftGet.SingleInstance", out bool created);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "SwiftGet.ShowWindow");
        if (!created)
        {
            // Already running: ask the first instance to show its window, then quit.
            _showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _main?.ShowFromTray());
        }) { IsBackground = true };
        listener.Start();

        DispatcherUnhandledException += (_, a) =>
        {
            Dialogs.Info(null, a.Exception.Message, warning: true);
            a.Handled = true;
        };
        SessionEnding += (_, _) => ExitApp();

        _manager = new DownloadManager();
        Loc.I.SetLanguage(_manager.Settings.Language);
        ThemeManager.Apply(_manager.Settings.Theme);
        ThemeManager.WatchSystem();

        _main = new MainWindow(_manager);
        if (!e.Args.Contains("--minimized")) _main.Show();
        if (e.Args.Contains("--browser-setup")) Dispatcher.BeginInvoke(() => _main.OpenSettings(3));

        _bridge = new BrowserBridge(_manager.Settings.BridgePort);
        _bridge.DownloadRequested += r => Dispatcher.BeginInvoke(() => _main.HandleBrowserRequest(r));
        try
        {
            _bridge.Start();
            _main.SetBridgeStatus(true);
        }
        catch
        {
            _main.SetBridgeStatus(false);
        }
    }

    public void ExitApp()
    {
        if (_exited) return;
        _exited = true;
        _bridge?.Dispose();
        _manager?.Shutdown();
        _main?.DisposeTray();
        _mutex?.Dispose();
        Shutdown();
    }
}
