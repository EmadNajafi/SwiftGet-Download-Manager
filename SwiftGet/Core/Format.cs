using System.Globalization;

namespace SwiftGet.Core;

public static class Format
{
    // Left-to-right mark: keeps "12.5 MB" readable inside right-to-left text.
    const string Lrm = "‎";

    public static string Size(long bytes)
    {
        if (bytes < 0) return Loc.T("Unknown");
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return Lrm + (i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}");
    }

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

    public static string Eta(TimeSpan t)
    {
        if (t.TotalDays >= 1) return Lrm + $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return Lrm + (t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}");
    }

    /// <summary>Solar Hijri date in Persian, ISO-like date in English.</summary>
    public static string Date(DateTime d)
    {
        if (!Loc.I.IsRtl) return d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var pc = new PersianCalendar();
        return Lrm + $"{pc.GetYear(d)}/{pc.GetMonth(d):00}/{pc.GetDayOfMonth(d):00} {d:HH:mm}";
    }

    public static string FileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name)) return SanitizeFileName(name);
            if (!string.IsNullOrWhiteSpace(uri.Host)) return SanitizeFileName(uri.Host + ".download");
        }
        return "download";
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Trim('"').Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 200)
        {
            var ext = Path.GetExtension(clean);
            clean = clean[..(200 - ext.Length)] + ext;
        }
        return string.IsNullOrWhiteSpace(clean) ? "download" : clean;
    }

    /// <summary>Returns path, or "name (1).ext", "name (2).ext"... if it already exists.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
