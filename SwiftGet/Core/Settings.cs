using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftGet.Core;

public sealed class ScheduleSettings
{
    public bool StartEnabled { get; set; }
    public string StartTime { get; set; } = "02:00";
    public bool StopEnabled { get; set; }
    public string StopTime { get; set; } = "08:00";
    /// <summary>Indexed by DayOfWeek (0 = Sunday).</summary>
    public bool[] Days { get; set; } = { true, true, true, true, true, true, true };
    public bool ShutdownWhenDone { get; set; }
}

public sealed class AppSettings
{
    public string DefaultFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SwiftGet");
    public bool UseCategoryFolders { get; set; } = true;
    public int Connections { get; set; } = 8;
    public int MaxConcurrent { get; set; } = 3;
    public int MaxRetries { get; set; } = 5;
    public int SpeedLimitKBps { get; set; }
    public bool ShowDialogForBrowser { get; set; } = true;
    public int BridgePort { get; set; } = 47321;
    public bool ShowCompleteDialog { get; set; } = true;
    public bool ShowProgressWindow { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Set once the first-run "start with Windows" default has been applied.</summary>
    public bool AutostartInitialized { get; set; }
    /// <summary>Set once queues from 2.0.0–2.0.2 (2–3 files at a time) were switched to one file at a time.</summary>
    public bool QueuesOneByOne { get; set; }
    /// <summary>"en", "fa", or empty for the Windows language.</summary>
    public string Language { get; set; } = "";
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public ScheduleSettings Schedule { get; set; } = new();

    // ---- connection
    /// <summary>"System" (Windows settings), "None" or "Manual".</summary>
    public string ProxyMode { get; set; } = "System";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUser { get; set; } = "";
    /// <summary>DPAPI-encrypted (see <see cref="Secret"/>).</summary>
    public string ProxyPassword { get; set; } = "";
    public List<SiteLogin> SiteLogins { get; set; } = new();

    // ---- clipboard
    public bool MonitorClipboard { get; set; } = true;
    public string ClipboardExtensions { get; set; } =
        "zip rar 7z tar gz iso exe msi apk dmg mp4 mkv avi mov webm mp3 flac m4a pdf epub m3u8";

    // ---- safety
    /// <summary>Mark downloaded files as coming from the internet, like browsers do (Windows SmartScreen checks them).</summary>
    public bool MarkOfTheWeb { get; set; } = true;
    public bool ScanAfterDownload { get; set; }
    /// <summary>Empty = Microsoft Defender. Otherwise an antivirus program; %1 in the arguments is the file.</summary>
    public string ScannerPath { get; set; } = "";
    public string ScannerArgs { get; set; } = "\"%1\"";

    // ---- streams
    /// <summary>ffmpeg.exe for converting video streams to MP4 (optional; found automatically on PATH).</summary>
    public string FfmpegPath { get; set; } = "";

    // ---- updates
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string SkippedVersion { get; set; } = "";
}

/// <summary>User name and password for a site (HTTP authentication or FTP).</summary>
public sealed class SiteLogin
{
    /// <summary>Host name, e.g. "files.example.com" or "*.example.com".</summary>
    public string Host { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>DPAPI-encrypted.</summary>
    public string Password { get; set; } = "";

    public bool Matches(Uri uri)
    {
        var h = Host.Trim().ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        if (h.StartsWith("*.")) return host == h[2..] || host.EndsWith(h[1..]);
        return host == h;
    }
}

/// <summary>JSON files under %APPDATA%\SwiftGet.</summary>
public static class Storage
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>%APPDATA%\SwiftGet, or the folder in SWIFTGET_DATA_DIR (handy for testing).</summary>
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("SWIFTGET_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwiftGet");

    public static T? Load<T>(string name)
    {
        try
        {
            var path = Path.Combine(Dir, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch
        {
            return default;
        }
    }

    public static void Save<T>(string name, T value) => WriteText(name, JsonSerializer.Serialize(value, Json));

    public static void WriteText(string name, string text)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
