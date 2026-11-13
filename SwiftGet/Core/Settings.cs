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
    /// <summary>"en", "fa", or empty for the Windows language.</summary>
    public string Language { get; set; } = "";
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public ScheduleSettings Schedule { get; set; } = new();
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
