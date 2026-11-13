using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using SwiftGet.Models;

namespace SwiftGet.Core;

/// <summary>Owns the download list, running engines, the queues and the scheduler. UI thread only.</summary>
public sealed class DownloadManager
{
    const string ItemsFile = "downloads.json";
    const string SettingsFile = "settings.json";
    const string QueuesFile = "queues.json";

    readonly Dictionary<Guid, DownloadEngine> _engines = new();
    readonly HashSet<Guid> _userPaused = new();
    readonly DispatcherTimer _tick;
    readonly DispatcherTimer _scheduleTimer;
    int _tickCount;

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public ObservableCollection<DownloadQueue> Queues { get; } = new();
    public AppSettings Settings { get; private set; }
    public SpeedLimiter Limiter { get; } = new();

    public event Action<DownloadItem>? ItemCompleted;
    public event Action<string, string>? Notify;
    public event Action? QueueStateChanged;
    /// <summary>A queue finished and its finish action (shut down, sleep…) should run.</summary>
    public event Action<DownloadQueue, QueueFinishAction>? QueueFinishActionRequested;
    /// <summary>Raised for downloads the user starts by hand, so the UI can show the progress window.</summary>
    public event Action<DownloadItem>? ProgressRequested;

    public int ActiveCount => _engines.Count;
    public double TotalSpeed => _engines.Values.Sum(e => e.Item.Speed);
    public bool IsRunning(DownloadItem item) => _engines.ContainsKey(item.Id);
    public bool AnyQueueRunning => Queues.Any(q => q.IsRunning);
    public DownloadQueue MainQueue => Queues.First(q => q.IsMain);
    public DownloadQueue? FindQueue(Guid? id) => id == null ? null : Queues.FirstOrDefault(q => q.Id == id);

    public DownloadManager()
    {
        Settings = Storage.Load<AppSettings>(SettingsFile) ?? new AppSettings();
        Limiter.BytesPerSecond = Settings.SpeedLimitKBps * 1024L;
        Http.Configure(Settings);

        LoadQueues();
        if (!Settings.QueuesOneByOne)
        {
            // earlier versions started queues with 2–3 files at once; the expected behaviour is one after another
            foreach (var q in Queues) q.MaxConcurrent = 1;
            Settings.QueuesOneByOne = true;
            SaveQueues();
            Storage.Save(SettingsFile, Settings);
        }

        foreach (var item in Storage.Load<List<DownloadItem>>(ItemsFile) ?? new())
        {
            if (item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading)
                item.Status = DownloadStatus.Paused;
            if (item.QueueId != null && FindQueue(item.QueueId) == null) item.QueueId = DownloadQueue.MainId;
            Items.Add(item);
        }

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();

        Loc.I.LanguageChanged += () =>
        {
            foreach (var i in Items) i.RefreshAll();
            foreach (var q in Queues) q.RefreshTexts();
        };

        // queues set to start with the app
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var q in Queues.Where(q => q.StartOnLaunch)) StartQueue(q);
        }, DispatcherPriority.ApplicationIdle);
    }

    void LoadQueues()
    {
        var loaded = Storage.Load<List<DownloadQueue>>(QueuesFile);
        if (loaded != null) foreach (var q in loaded) Queues.Add(q);

        if (!Queues.Any(q => q.IsMain))
        {
            // first run, or upgrade from 1.0/1.1: carry the old single-queue settings over to the main queue
            var s = Settings.Schedule;
            Queues.Insert(0, new DownloadQueue
            {
                Id = DownloadQueue.MainId,
                Name = "Main queue",
                MaxConcurrent = 1,
                StartEnabled = s.StartEnabled,
                StartTime = s.StartTime,
                StopEnabled = s.StopEnabled,
                StopTime = s.StopTime,
                Days = s.Days.Length == 7 ? s.Days : new[] { true, true, true, true, true, true, true },
                FinishAction = s.ShutdownWhenDone ? QueueFinishAction.Shutdown : QueueFinishAction.None,
            });
            SaveQueues();
        }
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
        if (item.QueueId != null) item.QueueOrder = NextOrder(item.QueueId.Value);
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
        var parts = item.StreamPartsDir;
        wait.ContinueWith(_ =>
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { if (Directory.Exists(parts)) Directory.Delete(parts, true); } catch { }
        });

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
        lock (item.SyncRoot)
        {
            item.Segments = new List<Segment>();
            item.StreamDone = 0;
            item.StreamBytes = 0;
        }
        try { if (Directory.Exists(item.StreamPartsDir)) Directory.Delete(item.StreamPartsDir, true); } catch { }
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
            if (FindQueue(e.Item.QueueId) is { } q) q.CompletedThisRun++;
            ItemCompleted?.Invoke(e.Item);
            ScanIfEnabled(e.Item);
        }
        Save();
        Pump();
    }

    // ------------------------------------------------------------------ queue membership and order

    int NextOrder(Guid queueId) =>
        Items.Where(i => i.QueueId == queueId).Select(i => i.QueueOrder).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Files of a queue in the order they will be downloaded.</summary>
    public List<DownloadItem> ItemsOf(DownloadQueue q) =>
        Items.Where(i => i.QueueId == q.Id).OrderBy(i => i.QueueOrder).ThenBy(i => i.DateAdded).ToList();

    /// <summary>Moves a download into a queue (or out of all queues when <paramref name="queue"/> is null).</summary>
    public void SetQueue(DownloadItem item, DownloadQueue? queue)
    {
        if (item.QueueId == queue?.Id) return;
        if (queue != null)
        {
            item.QueueOrder = NextOrder(queue.Id);
            item.QueueId = queue.Id;
            if (!IsRunning(item) && item.Status is DownloadStatus.Paused or DownloadStatus.Error)
                item.Status = DownloadStatus.Queued;
        }
        else
        {
            item.QueueId = null;
            if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        }
        Save();
        Pump();
    }

    /// <summary>Moves a download one place up (-1) or down (+1) in its queue.</summary>
    public void Move(DownloadItem item, int direction)
    {
        if (FindQueue(item.QueueId) is not { } q) return;
        var list = ItemsOf(q);
        for (int i = 0; i < list.Count; i++) list[i].QueueOrder = i + 1;   // normalise
        int index = list.IndexOf(item), target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count) return;
        (list[index].QueueOrder, list[target].QueueOrder) = (list[target].QueueOrder, list[index].QueueOrder);
        Save();
    }

    // ------------------------------------------------------------------ queues

    public DownloadQueue CreateQueue(string name)
    {
        var q = new DownloadQueue { Name = name };
        Queues.Add(q);
        SaveQueues();
        return q;
    }

    public void DeleteQueue(DownloadQueue q)
    {
        if (q.IsMain) return;
        StopQueue(q);
        foreach (var item in Items.Where(i => i.QueueId == q.Id).ToList())
        {
            item.QueueId = null;
            if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        }
        Queues.Remove(q);
        SaveQueues();
        Save();
        QueueStateChanged?.Invoke();
    }

    public void StartQueue(DownloadQueue q)
    {
        foreach (var item in Items.Where(i => i.QueueId == q.Id))
        {
            _userPaused.Remove(item.Id);
            // files that failed earlier (e.g. the server was busy) get another try
            if (item.Status == DownloadStatus.Error)
            {
                item.Status = DownloadStatus.Queued;
                item.ErrorMessage = null;
            }
        }
        if (!q.IsRunning) q.CompletedThisRun = 0;
        q.IsRunning = true;
        QueueStateChanged?.Invoke();
        Pump();
    }

    public void StopQueue(DownloadQueue q)
    {
        q.IsRunning = false;
        foreach (var e in _engines.Values.Where(e => e.Item.QueueId == q.Id).ToList()) e.Stop();
        QueueStateChanged?.Invoke();
    }

    void Pump()
    {
        foreach (var q in Queues.Where(q => q.IsRunning).ToList())
        {
            var files = ItemsOf(q);
            int active = files.Count(i => _engines.ContainsKey(i.Id));
            foreach (var item in files)
            {
                if (active >= Math.Max(1, q.MaxConcurrent)) break;
                if (_engines.ContainsKey(item.Id) || _userPaused.Contains(item.Id)) continue;
                if (item.Status is not (DownloadStatus.Queued or DownloadStatus.Paused)) continue;
                Start(item);
                active++;
            }

            if (active == 0) FinishQueue(q);
        }
    }

    void FinishQueue(DownloadQueue q)
    {
        q.IsRunning = false;
        QueueStateChanged?.Invoke();
        Notify?.Invoke(Loc.T("NotifyQueueTitle"), Loc.F("NotifyQueueDoneNamed", q.DisplayName));
        if (q.CompletedThisRun > 0 && q.FinishAction != QueueFinishAction.None)
            QueueFinishActionRequested?.Invoke(q, q.FinishAction);
    }

    // ------------------------------------------------------------------ refresh address

    /// <summary>
    /// Gives a download a new address (e.g. when the old link expired) and continues from where it stopped.
    /// Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> RefreshAddressAsync(DownloadItem item, string url, string? referrer = null, string? cookies = null)
    {
        try
        {
            var probe = await DownloadEngine.ProbeAsync(url, referrer ?? item.Referrer, cookies ?? item.Cookies, item.UserAgent, CancellationToken.None);
            if (!item.IsStream && item.TotalSize > 0 && probe.Size > 0 && probe.Size != item.TotalSize)
                return Loc.F("RefreshSizeMismatch", Format.Size(probe.Size), Format.Size(item.TotalSize));
            bool wasRunning = IsRunning(item);
            if (_engines.TryGetValue(item.Id, out var e))
            {
                e.Stop();
                await e.RunningTask;
            }
            item.Url = url;
            if (referrer != null) item.Referrer = referrer;
            if (cookies != null) item.Cookies = cookies;
            if (item.Status == DownloadStatus.Error) item.Status = DownloadStatus.Paused;
            Save();
            if (wasRunning || item.Status != DownloadStatus.Completed) Start(item);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ synchronization queues

    bool _syncing;

    async Task SyncQueuesAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            foreach (var q in Queues.Where(q => q.SyncEnabled && DateTime.Now - q.LastSync >= TimeSpan.FromHours(Math.Max(1, q.SyncIntervalHours))).ToList())
            {
                q.LastSync = DateTime.Now;
                bool changedAny = false;
                foreach (var item in ItemsOf(q).Where(i => i.Status == DownloadStatus.Completed && !i.IsStream))
                {
                    try
                    {
                        var p = await DownloadEngine.ProbeAsync(item.Url, item.Referrer, item.Cookies, item.UserAgent, CancellationToken.None);
                        bool changed = (p.Size > 0 && p.Size != item.TotalSize)
                                       || (p.ETag != null && item.RemoteETag != null && p.ETag != item.RemoteETag)
                                       || (p.LastModified != null && item.RemoteLastModified != null && p.LastModified > item.RemoteLastModified);
                        if (!changed) continue;
                        lock (item.SyncRoot) item.Segments = new List<Segment>();
                        item.ReplaceExisting = true;
                        item.DateCompleted = null;
                        item.Status = DownloadStatus.Queued;
                        changedAny = true;
                        Notify?.Invoke(Loc.T("SyncTitle"), Loc.F("SyncChanged", item.FileName));
                    }
                    catch
                    {
                        // server not reachable right now; try again next time
                    }
                }
                SaveQueues();
                Save();
                if (changedAny) StartQueue(q);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ------------------------------------------------------------------ antivirus

    void ScanIfEnabled(DownloadItem item)
    {
        if (!Settings.ScanAfterDownload || !File.Exists(item.FullPath)) return;
        var path = item.FullPath;
        var name = item.FileName;
        var s = Settings;
        Task.Run(async () =>
        {
            string exe, args;
            if (string.IsNullOrWhiteSpace(s.ScannerPath))
            {
                exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
                args = $"-Scan -ScanType 3 -File \"{path}\"";
            }
            else
            {
                exe = s.ScannerPath;
                args = (s.ScannerArgs ?? "\"%1\"").Replace("%1", path);
            }
            if (!File.Exists(exe)) return;
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
                await p.WaitForExitAsync();
                // Microsoft Defender returns 2 when it found a threat
                if (p.ExitCode == 2 || (!string.IsNullOrWhiteSpace(s.ScannerPath) && p.ExitCode != 0))
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Notify?.Invoke(Loc.T("ScanThreatTitle"), Loc.F("ScanThreat", name)));
            }
            catch { }
        });
    }

    // ------------------------------------------------------------------ scheduler

    void CheckSchedule()
    {
        var now = DateTime.Now;
        _ = SyncQueuesAsync();
        foreach (var q in Queues.ToList())
        {
            bool today = q.StartMode == QueueStartMode.Once
                ? q.OnceDate.Date == now.Date
                : q.Days.Length == 7 && q.Days[(int)now.DayOfWeek];

            if (q.StartEnabled && today && TimeSpan.TryParse(q.StartTime, out var start))
            {
                DateTime? last = q.LastStartFire;
                if (Due(now, start, ref last)) StartQueue(q);
                q.LastStartFire = last;
            }

            // the stop time applies on the days the queue is scheduled (or every day when it has no start time)
            bool stopToday = !q.StartEnabled || today;
            if (q.StopEnabled && stopToday && TimeSpan.TryParse(q.StopTime, out var stop))
            {
                DateTime? last = q.LastStopFire;
                if (Due(now, stop, ref last) && q.IsRunning) StopQueue(q);
                q.LastStopFire = last;
            }
        }
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

    /// <summary>Writes downloads to a SwiftGet list file (JSON) that can be imported on another computer.</summary>
    public void ExportList(string path, IEnumerable<DownloadItem> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            lock (item.SyncRoot)
                arr.Add(JsonSerializer.SerializeToNode(item, Storage.Json));
        File.WriteAllText(path, arr.ToJsonString(Storage.Json));
    }

    /// <summary>Adds the downloads from a list file; ones already in the list are skipped. Returns how many were added.</summary>
    public int ImportList(string path)
    {
        var list = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(path), Storage.Json) ?? new();
        int added = 0;
        foreach (var item in list)
        {
            if (Items.Any(i => i.Id == item.Id || (i.Url == item.Url && i.FileName == item.FileName))) continue;
            if (item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading) item.Status = DownloadStatus.Paused;
            if (item.QueueId != null && FindQueue(item.QueueId) == null) item.QueueId = null;
            Items.Add(item);
            added++;
        }
        Save();
        return added;
    }

    public void SaveQueues()
    {
        try { Storage.Save(QueuesFile, Queues.ToList()); } catch { }
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Limiter.BytesPerSecond = settings.SpeedLimitKBps * 1024L;
        Http.Configure(settings);
        Storage.Save(SettingsFile, settings);
        Pump();
    }

    /// <summary>Call after a queue's settings change (parallel limit, etc.).</summary>
    public void QueueSettingsChanged()
    {
        SaveQueues();
        Pump();
        QueueStateChanged?.Invoke();
    }

    public void Shutdown()
    {
        _tick.Stop();
        _scheduleTimer.Stop();
        var tasks = _engines.Values.Select(e => { e.Stop(); return e.RunningTask; }).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(3)); } catch { }
        foreach (var item in Items.Where(i => i.IsRunning)) item.Status = DownloadStatus.Paused;
        Save();
        SaveQueues();
        Storage.Save(SettingsFile, Settings);
    }
}
