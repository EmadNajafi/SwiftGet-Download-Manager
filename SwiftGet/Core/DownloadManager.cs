using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using SwiftGet.Models;

namespace SwiftGet.Core;

/// <summary>Owns the download list, running engines, the queue and the scheduler. UI thread only.</summary>
public sealed class DownloadManager
{
    const string ItemsFile = "downloads.json";
    const string SettingsFile = "settings.json";

    readonly Dictionary<Guid, DownloadEngine> _engines = new();
    readonly HashSet<Guid> _userPaused = new();
    readonly DispatcherTimer _tick;
    readonly DispatcherTimer _scheduleTimer;
    int _tickCount;
    int _completedThisRun;
    DateTime? _lastStartFire, _lastStopFire;

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public AppSettings Settings { get; private set; }
    public SpeedLimiter Limiter { get; } = new();
    public bool QueueRunning { get; private set; }

    public event Action<DownloadItem>? ItemCompleted;
    public event Action<string, string>? Notify;
    public event Action? QueueStateChanged;

    public int ActiveCount => _engines.Count;
    public double TotalSpeed => _engines.Values.Sum(e => e.Item.Speed);
    public bool IsRunning(DownloadItem item) => _engines.ContainsKey(item.Id);

    public DownloadManager()
    {
        Settings = Storage.Load<AppSettings>(SettingsFile) ?? new AppSettings();
        Limiter.BytesPerSecond = Settings.SpeedLimitKBps * 1024L;

        foreach (var item in Storage.Load<List<DownloadItem>>(ItemsFile) ?? new())
        {
            if (item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading)
                item.Status = DownloadStatus.Paused;
            Items.Add(item);
        }

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();

        Loc.I.LanguageChanged += () => { foreach (var i in Items) i.RefreshAll(); };
    }

    // ------------------------------------------------------------------ items

    public DownloadItem CreateItem(string url, string? fileName, string? referrer = null, string? cookies = null, string? userAgent = null)
    {
        bool auto = string.IsNullOrWhiteSpace(fileName);
        var name = Format.SanitizeFileName(auto ? Format.FileNameFromUrl(url) : fileName!);
        var cat = Categories.Detect(name);
        return new DownloadItem
        {
            Url = url,
            FileName = name,
            AutoFileName = auto,
            Category = cat,
            SaveFolder = Categories.FolderFor(Settings, cat),
            Referrer = referrer,
            Cookies = cookies,
            UserAgent = userAgent,
        };
    }

    public void Add(DownloadItem item)
    {
        Items.Add(item);
        Save();
        Pump();
    }

    public void Start(DownloadItem item)
    {
        if (_engines.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return;
        _userPaused.Remove(item.Id);
        var engine = new DownloadEngine(item, Settings, Limiter);
        engine.Finished += e => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OnEngineFinished(e));
        _engines[item.Id] = engine;
        item.Status = DownloadStatus.Connecting;
        engine.Start();
    }

    /// <summary>Raised for downloads the user starts by hand, so the UI can show the progress window.</summary>
    public event Action<DownloadItem>? ProgressRequested;

    public void StartWithProgress(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;
        Start(item);
        if (Settings.ShowProgressWindow) ProgressRequested?.Invoke(item);
    }

    public void Pause(DownloadItem item)
    {
        _userPaused.Add(item.Id);
        if (_engines.TryGetValue(item.Id, out var e)) e.Stop();
        else if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
    }

    public void PauseAll()
    {
        foreach (var e in _engines.Values.ToList())
        {
            _userPaused.Add(e.Item.Id);
            e.Stop();
        }
    }

    public void SetQueued(DownloadItem item, bool inQueue)
    {
        item.InQueue = inQueue;
        if (inQueue && !IsRunning(item) && item.Status is DownloadStatus.Paused or DownloadStatus.Error)
            item.Status = DownloadStatus.Queued;
        if (!inQueue && item.Status == DownloadStatus.Queued)
            item.Status = DownloadStatus.Paused;
        Save();
        Pump();
    }

    public void Remove(DownloadItem item, bool deleteCompletedFile)
    {
        Items.Remove(item);
        var temp = item.TempPath;
        var full = item.FullPath;
        var wait = Task.CompletedTask;
        if (_engines.TryGetValue(item.Id, out var e))
        {
            e.Stop();
            wait = e.RunningTask;
            _engines.Remove(item.Id);
        }
        wait.ContinueWith(_ => { try { if (File.Exists(temp)) File.Delete(temp); } catch { } });

        if (deleteCompletedFile && item.Status == DownloadStatus.Completed && File.Exists(full))
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch { }
        }
        Save();
    }

    /// <summary>Clears progress and downloads the file again from the start.</summary>
    public void Restart(DownloadItem item)
    {
        if (IsRunning(item)) return;
        lock (item.SyncRoot) item.Segments = new List<Segment>();
        item.Status = DownloadStatus.Paused;
        item.DateCompleted = null;
        Start(item);
    }

    void OnEngineFinished(DownloadEngine e)
    {
        if (_engines.TryGetValue(e.Item.Id, out var current) && current == e)
            _engines.Remove(e.Item.Id);
        e.Item.Tick();
        if (e.Item.Status == DownloadStatus.Completed)
        {
            if (e.Item.InQueue) _completedThisRun++;
            ItemCompleted?.Invoke(e.Item);
        }
        Save();
        Pump();
    }

    // ------------------------------------------------------------------ queue

    public void StartQueue()
    {
        _userPaused.Clear();
        if (!QueueRunning) _completedThisRun = 0;
        QueueRunning = true;
        QueueStateChanged?.Invoke();
        Pump();
    }

    public void StopQueue()
    {
        QueueRunning = false;
        foreach (var e in _engines.Values.Where(e => e.Item.InQueue).ToList()) e.Stop();
        QueueStateChanged?.Invoke();
    }

    void Pump()
    {
        if (!QueueRunning) return;
        int active = Items.Count(i => i.InQueue && _engines.ContainsKey(i.Id));
        var pending = Items
            .Where(i => i.InQueue && !_engines.ContainsKey(i.Id) && !_userPaused.Contains(i.Id)
                        && i.Status is DownloadStatus.Queued or DownloadStatus.Paused)
            .OrderBy(i => i.DateAdded)
            .ToList();

        foreach (var item in pending)
        {
            if (active >= Math.Max(1, Settings.MaxConcurrent)) break;
            Start(item);
            active++;
        }

        if (active == 0)
        {
            QueueRunning = false;
            QueueStateChanged?.Invoke();
            Notify?.Invoke(Loc.T("NotifyQueueTitle"), Loc.T("NotifyQueueDone"));
            if (Settings.Schedule.ShutdownWhenDone && _completedThisRun > 0)
            {
                Notify?.Invoke(Loc.T("NotifyShutdownTitle"), Loc.T("NotifyShutdown"));
                try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false }); }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ scheduler

    void CheckSchedule()
    {
        var s = Settings.Schedule;
        var now = DateTime.Now;
        if (s.Days.Length != 7 || !s.Days[(int)now.DayOfWeek]) return;
        if (s.StartEnabled && TimeSpan.TryParse(s.StartTime, out var start) && Due(now, start, ref _lastStartFire))
            StartQueue();
        if (s.StopEnabled && TimeSpan.TryParse(s.StopTime, out var stop) && Due(now, stop, ref _lastStopFire))
            StopQueue();
    }

    static bool Due(DateTime now, TimeSpan at, ref DateTime? last)
    {
        var target = now.Date + at;
        if (now >= target && now < target.AddMinutes(2) && last?.Date != now.Date)
        {
            last = now;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ persistence

    void OnTick()
    {
        foreach (var e in _engines.Values) e.Item.Tick();
        if (++_tickCount % 10 == 0 && _engines.Count > 0) Save();
    }

    public void Save()
    {
        try
        {
            var arr = new JsonArray();
            foreach (var item in Items)
                lock (item.SyncRoot)
                    arr.Add(JsonSerializer.SerializeToNode(item, Storage.Json));
            Storage.WriteText(ItemsFile, arr.ToJsonString(Storage.Json));
        }
        catch { }
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Limiter.BytesPerSecond = settings.SpeedLimitKBps * 1024L;
        Storage.Save(SettingsFile, settings);
        Pump();
    }

    public void Shutdown()
    {
        _tick.Stop();
        _scheduleTimer.Stop();
        var tasks = _engines.Values.Select(e => { e.Stop(); return e.RunningTask; }).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(3)); } catch { }
        foreach (var item in Items.Where(i => i.IsRunning)) item.Status = DownloadStatus.Paused;
        Save();
        Storage.Save(SettingsFile, Settings);
    }
}
