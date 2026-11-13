using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SwiftGet.Core;

namespace SwiftGet.Models;

public enum QueueStartMode { Daily, Once }

public enum QueueFinishAction { None, ExitApp, Sleep, Hibernate, Shutdown }

/// <summary>A download queue with its own schedule, parallel limit and finish action.</summary>
public sealed class DownloadQueue : INotifyPropertyChanged
{
    public static readonly Guid MainId = new("3b9f2a6e-0c41-4d7a-9a55-6f1f6c2e8d01");

    string _name = "";
    bool _isRunning;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { _name = value; Raise(); Raise(nameof(DisplayName)); } }

    public int MaxConcurrent { get; set; } = 2;
    public bool StartOnLaunch { get; set; }

    public bool StartEnabled { get; set; }
    public QueueStartMode StartMode { get; set; } = QueueStartMode.Daily;
    public DateTime OnceDate { get; set; } = DateTime.Today;
    public string StartTime { get; set; } = "02:00";
    /// <summary>Indexed by DayOfWeek (0 = Sunday).</summary>
    public bool[] Days { get; set; } = { true, true, true, true, true, true, true };

    public bool StopEnabled { get; set; }
    public string StopTime { get; set; } = "08:00";

    public QueueFinishAction FinishAction { get; set; } = QueueFinishAction.None;

    /// <summary>Synchronization: check finished files for changes on the server and download them again.</summary>
    public bool SyncEnabled { get; set; }
    public int SyncIntervalHours { get; set; } = 24;
    public DateTime LastSync { get; set; }

    // ---- runtime only
    [JsonIgnore] public bool IsMain => Id == MainId;
    [JsonIgnore] public string DisplayName => IsMain ? Loc.T("MainQueue") : Name;
    [JsonIgnore] public DateTime? LastStartFire { get; set; }
    [JsonIgnore] public DateTime? LastStopFire { get; set; }
    [JsonIgnore] public int CompletedThisRun { get; set; }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning != value) { _isRunning = value; Raise(); Raise(nameof(StateText)); } }
    }

    [JsonIgnore] public string StateText => Loc.T(IsRunning ? "QRunning" : "QStopped");

    public void RefreshTexts()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(StateText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
