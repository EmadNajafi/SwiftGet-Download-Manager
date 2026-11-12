using System.Windows.Media;

namespace SwiftGet.Core;

public sealed class CategoryInfo
{
    public CategoryInfo(string key, string glyph, uint rgb, string[] extensions)
    {
        Key = key;
        Glyph = glyph;
        Extensions = extensions;
        var c = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        Brush = new SolidColorBrush(c);
        Brush.Freeze();
        SoftBrush = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        SoftBrush.Freeze();
    }

    public string Key { get; }
    public string Glyph { get; }
    public string[] Extensions { get; }
    public SolidColorBrush Brush { get; }
    public SolidColorBrush SoftBrush { get; }
    public string Title => Loc.T("Cat" + Key);
}

public static class Categories
{
    public static readonly CategoryInfo[] All =
    {
        new("Video", "", 0x8B5CF6, new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "3gp", "ts", "mpg", "mpeg" }),
        new("Music", "", 0xEC4899, new[] { "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus" }),
        new("Documents", "", 0x3B82F6, new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "epub", "csv", "odt" }),
        new("Compressed", "", 0xF59E0B, new[] { "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "iso", "cab" }),
        new("Programs", "\uE770", 0x10B981, new[] { "exe", "msi", "apk", "dmg", "deb", "rpm", "appx", "msix", "bat", "jar" }),
        new("Images", "", 0x06B6D4, new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "tif", "tiff", "heic" }),
        new("Other", "", 0x64748B, Array.Empty<string>()),
    };

    public static CategoryInfo Get(string key) => All.FirstOrDefault(c => c.Key == key) ?? All[^1];

    public static string Detect(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return All.FirstOrDefault(c => c.Extensions.Contains(ext))?.Key ?? "Other";
    }

    public static string FolderFor(AppSettings settings, string key) =>
        settings.UseCategoryFolders ? Path.Combine(settings.DefaultFolder, key) : settings.DefaultFolder;
}
