using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using SwiftGet.Core;

namespace SwiftGet.Models;

public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Completed, Error }

/// <summary>One byte range of the file, fetched by one connection.</summary>
public sealed class Segment
{
    public long Start { get; set; }
    /// <summary>Inclusive end offset; -1 when the total size is unknown.</summary>
    public long End { get; set; }
    public long Downloaded { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public long Position => Start + Downloaded;
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    [JsonIgnore] public long Length => End < 0 ? -1 : End - Start + 1;
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    string _url = "", _fileName = "", _saveFolder = "", _category = "Other";
    string? _error;
    long _totalSize = -1;
    bool _resumeSupported;
    Guid? _queueId;
    DownloadStatus _status = DownloadStatus.Paused;
    DateTime? _dateCompleted;
    long _lastBytes = -1;
    DateTime _lastSample;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get => _url; set => Set(ref _url, value); }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>True while the file name is only a guess from the URL and the server may supply a better one.</summary>
    public bool AutoFileName { get; set; }

    public string FileName
    {
        get => _fileName;
        set { if (Set(ref _fileName, value)) Raise(nameof(FullPath)); }
    }

    public string SaveFolder
    {
        get => _saveFolder;
        set { if (Set(ref _saveFolder, value)) Raise(nameof(FullPath)); }
    }

    public string Category
    {
        get => _category;
        set
        {
            if (!Set(ref _category, value)) return;
            Raise(nameof(CategoryTitle));
            Raise(nameof(CategoryGlyph));
            Raise(nameof(CategoryBrush));
            Raise(nameof(CategorySoftBrush));
        }
    }

    public long TotalSize
    {
        get => _totalSize;
        set { if (Set(ref _totalSize, value)) RaiseTexts(); }
    }

    public bool ResumeSupported { get => _resumeSupported; set => Set(ref _resumeSupported, value); }

    public DownloadStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(IsRunning)); RaiseTexts(); } }
    }

    public string? ErrorMessage
    {
        get => _error;
        set { if (Set(ref _error, value)) RaiseTexts(); }
    }

    public DateTime DateAdded { get; set; } = DateTime.Now;
    public DateTime? DateCompleted { get => _dateCompleted; set => Set(ref _dateCompleted, value); }
    /// <summary>The queue this download belongs to, or null.</summary>
    public Guid? QueueId
    {
        get => _queueId;
        set { if (Set(ref _queueId, value)) Raise(nameof(InQueue)); }
    }

    /// <summary>Position inside its queue (lower runs first).</summary>
    public int QueueOrder { get; set; }

    [JsonIgnore] public bool InQueue => _queueId != null;

    /// <summary>Reads the "InQueue" flag written by version 1.0/1.1 (those had a single queue).</summary>
    [JsonPropertyName("InQueue")]
    public bool LegacyInQueue
    {
        get => InQueue;
        set { if (value && _queueId == null) _queueId = DownloadQueue.MainId; }
    }
    public List<Segment> Segments { get; set; } = new();

    // ---- remote file info (used by the sync queue to notice changes)
    public DateTimeOffset? RemoteLastModified { get; set; }
    /// <summary>Set by the sync queue: overwrite the old copy instead of saving "name (1)".</summary>
    public bool ReplaceExisting { get; set; }
    public string? RemoteETag { get; set; }

    // ---- video streams (HLS): progress counted in parts instead of byte ranges
    public bool IsStream { get; set; }

    // ---- videos from sites like YouTube (downloaded with yt-dlp); Url is then the page address
    /// <summary>yt-dlp format choice (e.g. "bv[height=1080]+ba"); set only for site videos.</summary>
    public string? SiteFormat { get; set; }
    [JsonIgnore] public bool IsSiteVideo => !string.IsNullOrEmpty(SiteFormat);
    public int StreamTotal { get; set; }
    public int StreamDone { get; set; }
    public long StreamBytes { get; set; }
    [JsonIgnore] public int StreamActive { get; set; }
    /// <summary>Folder with the parts of a stream while it downloads.</summary>
    [JsonIgnore] public string StreamPartsDir => Path.Combine(SaveFolder, $".swiftget-{Id:N}");

    // ------------------------------------------------------------------ computed (not saved)

    [JsonIgnore] public object SyncRoot { get; } = new();
    [JsonIgnore] public string FullPath => Path.Combine(SaveFolder, FileName);
    [JsonIgnore] public string TempPath => FullPath + ".swg";
    [JsonIgnore] public bool IsRunning => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;
    [JsonIgnore] public double Speed { get; private set; }

    [JsonIgnore] public string CategoryTitle => Categories.Get(Category).Title;
    [JsonIgnore] public string CategoryGlyph => Categories.Get(Category).Glyph;
    [JsonIgnore] public Brush CategoryBrush => Categories.Get(Category).Brush;
    [JsonIgnore] public Brush CategorySoftBrush => Categories.Get(Category).SoftBrush;

    [JsonIgnore]
    public long Downloaded
    {
        get { lock (SyncRoot) return IsStream ? StreamBytes : Segments.Sum(s => s.Downloaded); }
    }

    [JsonIgnore]
    public int ActiveConnections
    {
        get { lock (SyncRoot) return IsStream ? StreamActive : Segments.Count(s => s.Active); }
    }

    [JsonIgnore]
    public double Progress =>
        Status == DownloadStatus.Completed ? 100 :
        IsStream ? (StreamTotal > 0 ? Math.Min(99.9, StreamDone * 100.0 / StreamTotal) : 0) :
        TotalSize > 0 ? Math.Min(100, Downloaded * 100.0 / TotalSize) : 0;

    [JsonIgnore] public string SizeText => TotalSize > 0 ? (IsStream && Status != DownloadStatus.Completed ? "~" : "") + Format.Size(TotalSize) : Loc.T("Unknown");
    [JsonIgnore] public string DownloadedText => Format.Size(Downloaded);
    [JsonIgnore] public string SpeedText => Status == DownloadStatus.Downloading ? Format.Speed(Speed) : "—";
    [JsonIgnore] public string DateAddedText => Format.Date(DateAdded);

    [JsonIgnore]
    public string ProgressText => TotalSize > 0
        ? Loc.F("SizeOf", Format.Size(Downloaded), Format.Size(TotalSize))
        : Format.Size(Downloaded);

    [JsonIgnore]
    public string EtaText
    {
        get
        {
            if (Status != DownloadStatus.Downloading || Speed < 1 || TotalSize <= 0) return "—";
            return Format.Eta(TimeSpan.FromSeconds(Math.Max(0, TotalSize - Downloaded) / Speed));
        }
    }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => Loc.T("StatusQueued"),
        DownloadStatus.Connecting => ErrorMessage != null ? Loc.T("StatusRetrying") : Loc.T("StatusConnecting"),
        DownloadStatus.Downloading => Loc.T("StatusDownloading"),
        DownloadStatus.Paused => Downloaded > 0 ? Loc.T("StatusPaused") : Loc.T("StatusReady"),
        DownloadStatus.Completed => Loc.T("StatusCompleted"),
        DownloadStatus.Error => Loc.T("StatusError"),
        _ => ""
    };

    /// <summary>Short label in the coloured pill on each row.</summary>
    [JsonIgnore]
    public string PillText => Status switch
    {
        DownloadStatus.Downloading when TotalSize > 0 => $"\u200E{Progress:0}%",
        DownloadStatus.Paused when TotalSize > 0 && Downloaded > 0 => $"\u200E{Progress:0}%",
        _ => StatusText
    };

    /// <summary>"Status" column of the download table: percent while unfinished, otherwise the state.</summary>
    [JsonIgnore]
    public string GridStatusText => Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Paused when TotalSize > 0 && Downloaded > 0 => $"‎{Progress:0.00}%",
        _ => StatusText
    };

    [JsonIgnore] public string GridEtaText => EtaText == "—" ? "" : EtaText;
    [JsonIgnore] public string GridSpeedText => Status == DownloadStatus.Downloading ? Format.Speed(Speed) : "";

    /// <summary>"Downloaded" line of the progress window, e.g. "221.6 MB (23.40%)".</summary>
    [JsonIgnore]
    public string DownloadedDetailText => TotalSize > 0
        ? $"{Format.Size(Downloaded)}   ·   \u200E{Progress:0.00}%"
        : Format.Size(Downloaded);

    /// <summary>Second line of each row.</summary>
    [JsonIgnore]
    public string SublineText
    {
        get
        {
            const string dot = "   ·   ";
            return Status switch
            {
                DownloadStatus.Downloading => ProgressText + dot + Format.Speed(Speed) +
                                              (EtaText != "—" ? dot + Loc.F("TimeLeft", EtaText) : ""),
                DownloadStatus.Connecting when ErrorMessage != null => Loc.T("StatusRetrying") + "   ·   " + ErrorMessage,
                DownloadStatus.Connecting => ErrorMessage != null ? Loc.T("StatusRetrying") : Loc.T("StatusConnecting"),
                DownloadStatus.Completed => SizeText + dot + Format.Date(DateCompleted ?? DateAdded),
                DownloadStatus.Error => ErrorMessage ?? Loc.T("StatusError"),
                DownloadStatus.Queued => SizeText + dot + Loc.T("StatusQueued"),
                _ => (Downloaded > 0 ? ProgressText : SizeText) + dot + StatusText,
            };
        }
    }

    /// <summary>Glyph of the main button on each row: pause, resume, or open.</summary>
    [JsonIgnore]
    public string PrimaryGlyph => IsRunning ? "" : Status == DownloadStatus.Completed ? "" : "";

    [JsonIgnore]
    public string PrimaryTip => IsRunning ? Loc.T("Pause") : Status == DownloadStatus.Completed ? Loc.T("MenuOpenFile") : Loc.T("Resume");

    public List<Segment> SnapshotSegments()
    {
        lock (SyncRoot)
            return Segments.Select(s => new Segment { Start = s.Start, End = s.End, Downloaded = s.Downloaded, Active = s.Active }).ToList();
    }

    /// <summary>Called periodically on the UI thread: updates speed and refreshes bindings.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        var bytes = Downloaded;
        if (Status == DownloadStatus.Downloading && _lastBytes >= 0)
        {
            var sec = (now - _lastSample).TotalSeconds;
            if (sec > 0)
            {
                var instant = Math.Max(0, (bytes - _lastBytes) / sec);
                Speed = Speed <= 0 ? instant : Speed * 0.7 + instant * 0.3;
            }
        }
        else if (Status != DownloadStatus.Downloading)
        {
            Speed = 0;
        }
        _lastBytes = bytes;
        _lastSample = now;

        Raise(nameof(Downloaded));
        Raise(nameof(Speed));
        Raise(nameof(ActiveConnections));
        RaiseTexts();
    }

    /// <summary>Refreshes every binding (used after a language change).</summary>
    public void RefreshAll() => Raise(string.Empty);

    void RaiseTexts()
    {
        foreach (var name in TextProperties) Raise(name);
    }

    static readonly string[] TextProperties =
    {
        nameof(Progress), nameof(SizeText), nameof(DownloadedText), nameof(SpeedText), nameof(DateAddedText),
        nameof(ProgressText), nameof(EtaText), nameof(StatusText), nameof(PillText), nameof(SublineText),
        nameof(PrimaryGlyph), nameof(PrimaryTip),
        nameof(GridStatusText), nameof(GridEtaText), nameof(GridSpeedText), nameof(DownloadedDetailText),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    void Raise(string name)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess())
            d.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
