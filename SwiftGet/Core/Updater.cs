using System.Diagnostics;
using System.Text.Json;

namespace SwiftGet.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string? SetupUrl, long SetupSize, string PageUrl);

/// <summary>Checks GitHub Releases for a newer SwiftGet and installs it.</summary>
public static class Updater
{
    const string LatestApi = "https://api.github.com/repos/EmadNajafi/SwiftGet-Download-Manager/releases/latest";

    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    /// <summary>True when SwiftGet was installed with the installer (an uninstaller sits next to it).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The newest release if it is newer than this version, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        req.Headers.TryAddWithoutValidation("User-Agent", $"SwiftGet/{Current}");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= Current) return null;

        string? setup = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("SwiftGet-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                setup = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
            }
        }
        return new UpdateInfo(version, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            setup, size, root.GetProperty("html_url").GetString() ?? "");
    }

    public static async Task<string> DownloadSetupAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SwiftGet-Setup-{info.Version}.exe");
        using var req = new HttpRequestMessage(HttpMethod.Get, info.SetupUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", $"SwiftGet/{Current}");
        using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.SetupSize;
        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await net.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress.Report(done * 100.0 / total);
        }
        return path;
    }

    /// <summary>Runs the installer silently; it closes SwiftGet, updates it and starts it again.</summary>
    public static void Install(string setupPath)
    {
        // start the installer two seconds later, when this copy of SwiftGet has already closed
        var args = $"/c ping 127.0.0.1 -n 3 >nul & start \"\" \"{setupPath}\" /SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1";
        Process.Start(new ProcessStartInfo("cmd.exe", args) { CreateNoWindow = true, UseShellExecute = false });
    }
}
