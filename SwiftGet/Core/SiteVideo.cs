using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SwiftGet.Models;

namespace SwiftGet.Core;

/// <summary>One quality the user can pick, e.g. "1080p · MP4 · ~250 MB".</summary>
public sealed record VideoChoice(string Label, string Detail, string Format, string Ext, long Size, bool AudioOnly);

public sealed record VideoInfo(string Title, string PageUrl, double Duration, string? Thumbnail, List<VideoChoice> Choices);

/// <summary>
/// Videos from sites such as YouTube, Vimeo or Instagram (Aparat is handled by the extension's stream detection). Those sites don't offer a plain file:
/// video and audio come in separate pieces. The free tool yt-dlp finds and downloads them, and ffmpeg
/// joins video and audio into one file. Both are downloaded once into %LOCALAPPDATA%\SwiftGet\tools.
/// </summary>
public static class SiteVideo
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";
    public const long YtDlpSize = 24L << 20, FfmpegSize = 87L << 20;

    public static string ToolsDir { get; } =
        Environment.GetEnvironmentVariable("SWIFTGET_TOOLS_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "tools");

    public static string YtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");
    public static string BundledFfmpeg => Path.Combine(ToolsDir, "ffmpeg", "ffmpeg.exe");

    // sites whose pages hold a video that only yt-dlp can download
    static readonly string[] Hosts =
    {
        "youtube.com", "youtu.be", "youtube-nocookie.com", "vimeo.com", "dailymotion.com", "dai.ly",
        "instagram.com", "twitter.com", "x.com", "facebook.com", "fb.watch", "tiktok.com", "twitch.tv", "soundcloud.com",
        "reddit.com", "bilibili.com", "ok.ru", "vk.com", "rumble.com", "streamable.com", "bitchute.com", "telewebion.com",
    };

    /// <summary>A page on a video site (youtube.com/watch?v=…), not a direct file link.</summary>
    public static bool IsSiteVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
        var host = u.Host.ToLowerInvariant();
        if (!Hosts.Any(h => host == h || host.EndsWith("." + h))) return false;
        if (host.EndsWith("googlevideo.com")) return false;
        return u.AbsolutePath.Length > 1;   // the home page itself is not a video
    }

    public static string? FindFfmpeg(AppSettings s) => Ffmpeg.Find(s);

    /// <summary>Size of the tools still to download (0 = ready).</summary>
    public static long MissingToolsSize(AppSettings s) =>
        (File.Exists(YtDlpPath) ? 0 : YtDlpSize) + (FindFfmpeg(s) != null ? 0 : FfmpegSize);

    // ------------------------------------------------------------------ tools

    static readonly SemaphoreSlim ToolsLock = new(1, 1);

    /// <summary>Downloads yt-dlp and ffmpeg when missing, and keeps yt-dlp up to date (sites change often).</summary>
    public static async Task EnsureToolsAsync(AppSettings s, IProgress<double>? progress, CancellationToken ct)
    {
        await ToolsLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ToolsDir);
            long total = MissingToolsSize(s), done = 0;
            void Report(long bytes) => progress?.Report(total > 0 ? Math.Min(1, (done + bytes) / (double)total) : 1);

            if (!File.Exists(YtDlpPath))
            {
                await DownloadFileAsync(YtDlpUrl, YtDlpPath, Report, ct);
                done += YtDlpSize;
            }
            else if (DateTime.Now - File.GetLastWriteTime(YtDlpPath) > TimeSpan.FromDays(3))
            {
                // yt-dlp updates itself; a failed update just keeps the current copy
                try
                {
                    var (_, _, _) = await RunAsync(new[] { "-U" }, null, ct, TimeSpan.FromMinutes(2));
                    File.SetLastWriteTime(YtDlpPath, DateTime.Now);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }

            if (FindFfmpeg(s) == null) await DownloadFfmpegAsync(Report, ct);
            progress?.Report(1);
        }
        finally
        {
            ToolsLock.Release();
        }
    }

    /// <summary>Only ffmpeg (for video streams whose sound comes separately).</summary>
    public static async Task EnsureFfmpegAsync(AppSettings s, CancellationToken ct)
    {
        await ToolsLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ToolsDir);
            if (FindFfmpeg(s) == null) await DownloadFfmpegAsync(_ => { }, ct);
        }
        finally
        {
            ToolsLock.Release();
        }
    }

    static async Task DownloadFfmpegAsync(Action<long> report, CancellationToken ct)
    {
        var zip = Path.Combine(ToolsDir, "ffmpeg.zip");
        await DownloadFileAsync(FfmpegUrl, zip, report, ct);
        var dir = Path.Combine(ToolsDir, "ffmpeg");
        var tmp = dir + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        using (var archive = ZipFile.OpenRead(zip))
        {
            // only bin\ (ffmpeg.exe, ffprobe.exe and their DLLs)
            foreach (var entry in archive.Entries.Where(e => e.FullName.Contains("/bin/") && e.Name.Length > 0 && !e.Name.StartsWith("ffplay")))
                entry.ExtractToFile(Path.Combine(tmp, entry.Name), overwrite: true);
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);   // ffmpeg.exe appears only once everything is in place
        File.Delete(zip);
    }

    static async Task DownloadFileAsync(string url, string path, Action<long> progress, CancellationToken ct)
    {
        var tmp = path + ".download";
        using (var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var net = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buf = new byte[1 << 16];
            long got = 0;
            int n;
            while ((n = await net.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                progress(got);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------------ video information

    public static async Task<VideoInfo> GetInfoAsync(AppSettings s, string url, string? cookies, CancellationToken ct)
    {
        var (code, output, error) = await RunWithCookiesAsync(s, url, cookies, new[] { "-J", "--no-playlist", "--no-warnings" }, null, ct, TimeSpan.FromMinutes(2));
        if (code != 0 || string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException(CleanError(error));

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        // a playlist link: take its first video
        if (Str(root, "_type") == "playlist" && root.TryGetProperty("entries", out var entries) && entries.GetArrayLength() > 0)
            root = entries[0];

        var title = Str(root, "title") ?? "video";
        var duration = Num(root, "duration");
        var formats = root.TryGetProperty("formats", out var fa) ? fa.EnumerateArray().Select(f => new Fmt(f, duration)).ToList() : new();
        if (formats.Count == 0 && Str(root, "url") != null) formats.Add(new Fmt(root, duration));
        formats = formats.Where(f => f.Protocol is not ("mhtml") && !(f.Note ?? "").Contains("storyboard")).ToList();

        var audio = formats.Where(f => f.AudioOnly).ToList();
        var bestM4a = audio.Where(f => f.Ext == "m4a").MaxBy(f => f.Bitrate) ?? audio.MaxBy(f => f.Bitrate);
        var choices = new List<VideoChoice>();

        foreach (var h in formats.Where(f => f.HasVideo && f.Height > 0).Select(f => f.Height).Distinct().OrderByDescending(h => h))
        {
            var videoOnly = formats.Where(f => f.VideoOnly && f.Height == h).ToList();
            var ordered = videoOnly.OrderByDescending(f => f.Protocol is "https" or "http").ThenByDescending(f => f.Bitrate).ToList();
            var pick = ordered.FirstOrDefault(f => f.Vcodec.StartsWith("avc1")) ?? ordered.FirstOrDefault();
            long size = pick != null
                ? pick.Size + (bestM4a?.Size ?? 0)
                : formats.Where(f => f.HasVideo && !f.VideoOnly && f.Height == h).MaxBy(f => f.Bitrate)?.Size ?? 0;
            var fps = (pick ?? formats.First(f => f.Height == h)).Fps;
            var label = $"{h}p" + (fps > 30 ? fps.ToString("0") : "") + (h >= 2160 ? " (4K)" : h >= 1440 ? " (2K)" : h >= 720 ? " (HD)" : "");
            choices.Add(new VideoChoice(label, "MP4" + (size > 0 ? " · ~" + Format.Size(size) : ""),
                $"bv[height={h}][vcodec^=avc1]+ba[ext=m4a]/bv[height={h}]+ba/b[height={h}]/bv*+ba/b", "mp4", size, false));
        }
        if (choices.Count == 0 && formats.Any(f => !f.AudioOnly))
        {
            var best = formats.Where(f => !f.AudioOnly).MaxBy(f => f.Bitrate);
            long size = (best?.Size ?? 0) + (best?.VideoOnly == true ? bestM4a?.Size ?? 0 : 0);
            choices.Add(new VideoChoice(Loc.T("VideoBest"), "MP4" + (size > 0 ? " · ~" + Format.Size(size) : ""), "bv*+ba/b", "mp4", size, false));
        }
        if (bestM4a != null)
        {
            var ext = bestM4a.Ext == "m4a" ? "m4a" : bestM4a.Ext;
            choices.Add(new VideoChoice(Loc.T("VideoAudioOnly"), ext.ToUpperInvariant() + (bestM4a.Size > 0 ? " · ~" + Format.Size(bestM4a.Size) : ""),
                "ba[ext=m4a]/ba", ext, bestM4a.Size, true));
        }
        if (choices.Count == 0) throw new InvalidOperationException(Loc.T("VideoNoFormats"));

        var page = Str(root, "webpage_url") ?? url;
        return new VideoInfo(title, page, duration, Str(root, "thumbnail"), choices);
    }

    sealed class Fmt
    {
        public string Ext, Vcodec, Acodec, Protocol;
        public string? Note;
        public int Height;
        public double Fps, Bitrate;
        public long Size;
        public Fmt(JsonElement f, double duration)
        {
            Ext = Str(f, "ext") ?? "";
            Vcodec = Str(f, "vcodec") ?? "";
            Acodec = Str(f, "acodec") ?? "";
            Protocol = Str(f, "protocol") ?? "";
            Note = Str(f, "format_note");
            Height = (int)Num(f, "height");
            Fps = Num(f, "fps");
            Bitrate = Num(f, "tbr");
            var size = Num(f, "filesize");
            if (size <= 0) size = Num(f, "filesize_approx");
            if (size <= 0 && Bitrate > 0 && duration > 0) size = Bitrate * 125 * duration;
            Size = (long)size;
        }
        public bool AudioOnly => Vcodec == "none" && Acodec != "none" && Acodec != "";
        public bool VideoOnly => Vcodec != "none" && Vcodec != "" && Acodec == "none";
        public bool HasVideo => Vcodec != "none";
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // ------------------------------------------------------------------ download

    /// <summary>Downloads the item's video into its parts folder and returns the finished file.</summary>
    public static async Task<string> DownloadAsync(AppSettings s, SpeedLimiter limiter, DownloadItem item, CancellationToken ct)
    {
        await EnsureToolsAsync(s, null, ct);
        var dir = item.StreamPartsDir;
        Directory.CreateDirectory(dir);
        try { File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.Hidden); } catch { }

        var args = new List<string>
        {
            "--no-playlist", "--newline", "--no-warnings", "--no-mtime", "--continue", "--no-colors",
            "-N", "4", "-f", item.SiteFormat!, "-o", Path.Combine(dir, "media.%(ext)s"),
            "--progress-template", "download:SGP|%(info.format_id)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s",
        };
        var ext = Path.GetExtension(item.FileName).TrimStart('.').ToLowerInvariant();
        if (ext is "mp4" or "mkv" or "webm") args.AddRange(new[] { "--merge-output-format", ext });
        if (FindFfmpeg(s) is { } ffmpeg) args.AddRange(new[] { "--ffmpeg-location", Path.GetDirectoryName(ffmpeg)! });
        if (limiter.BytesPerSecond > 0) args.AddRange(new[] { "-r", limiter.BytesPerSecond.ToString() });

        // progress: yt-dlp fetches video and audio one after the other
        var got = new Dictionary<string, long>();
        var totals = new Dictionary<string, long>();
        long estimate = item.TotalSize;
        lock (item.SyncRoot)
        {
            item.Segments = new List<Segment> { new() { Start = 0, End = Math.Max(0, estimate) - 1, Active = true } };
        }
        void OnLine(string line)
        {
            if (!line.StartsWith("SGP|")) return;
            var p = line.Split('|');
            if (p.Length < 5) return;
            var id = p[1];
            if (long.TryParse(p[2], out var d)) got[id] = d;
            if (long.TryParse(p[3], out var t) || long.TryParse(p[4].Split('.')[0], out t)) totals[id] = t;
            long sum = got.Values.Sum(), sumTotal = totals.Values.Sum();
            long total = Math.Max(sumTotal, estimate);
            lock (item.SyncRoot)
            {
                var seg = item.Segments[0];
                seg.End = Math.Max(total, sum) - 1;
                seg.Downloaded = sum;
            }
            if (total > 0 && item.TotalSize != total) item.TotalSize = total;
            if (item.Status != DownloadStatus.Downloading) item.Status = DownloadStatus.Downloading;
        }

        try
        {
            var (code, _, error) = await RunWithCookiesAsync(s, item.Url, item.Cookies, args, OnLine, ct, null);
            if (code != 0) throw new IOException(CleanError(error));
        }
        finally
        {
            lock (item.SyncRoot) item.Segments[0].Active = false;
        }

        var result = Directory.GetFiles(dir, "media.*")
            .Where(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^media\.f[\w-]+\."))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault() ?? throw new IOException(Loc.T("VideoNoFile"));
        return result;
    }

    // ------------------------------------------------------------------ running yt-dlp

    /// <summary>
    /// Runs yt-dlp. YouTube works best without browser cookies; when it asks for a sign-in (age-restricted
    /// or private videos) it runs again with them. Other sites (Instagram, …) get the cookies right away.
    /// </summary>
    static async Task<(int Code, string Output, string Error)> RunWithCookiesAsync(AppSettings s, string url, string? cookies,
        IEnumerable<string> args, Action<string>? onLine, CancellationToken ct, TimeSpan? timeout)
    {
        var baseArgs = args.Concat(ProxyArgs(s)).ToList();
        bool youtube = url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cookies) || youtube)
        {
            var first = await RunAsync(baseArgs.Append("--").Append(url), onLine, ct, timeout);
            if (first.Code == 0 || string.IsNullOrWhiteSpace(cookies) || !NeedsSignIn(first.Error)) return first;
        }
        var file = WriteCookieFile(url, cookies!);
        try
        {
            return await RunAsync(baseArgs.Concat(new[] { "--cookies", file, "--", url }), onLine, ct, timeout);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    static bool NeedsSignIn(string error) =>
        error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("age", StringComparison.OrdinalIgnoreCase) || error.Contains("private", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> ProxyArgs(AppSettings s)
    {
        if (s.ProxyMode == "None") return new[] { "--proxy", "" };
        if (s.ProxyMode == "Manual" && !string.IsNullOrWhiteSpace(s.ProxyHost))
        {
            var auth = string.IsNullOrWhiteSpace(s.ProxyUser) ? "" :
                Uri.EscapeDataString(s.ProxyUser) + ":" + Uri.EscapeDataString(Secret.Unprotect(s.ProxyPassword) ?? "") + "@";
            return new[] { "--proxy", $"http://{auth}{s.ProxyHost.Trim()}:{s.ProxyPort}" };
        }
        return Array.Empty<string>();   // yt-dlp uses the Windows proxy by itself
    }

    /// <summary>Browser cookies ("a=b; c=d") as a Netscape cookie file for yt-dlp.</summary>
    static string WriteCookieFile(string url, string cookies)
    {
        var host = new Uri(url).Host;
        var domain = "." + string.Join('.', host.Split('.').TakeLast(host.EndsWith(".co.uk") ? 3 : 2));
        var expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var pair in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = pair.IndexOf('=');
            if (i <= 0) continue;
            sb.Append($"{domain}\tTRUE\t/\tTRUE\t{expires}\t{pair[..i]}\t{pair[(i + 1)..]}\n");
        }
        var file = Path.Combine(Path.GetTempPath(), "swiftget-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    static async Task<(int Code, string Output, string Error)> RunAsync(IEnumerable<string> args, Action<string>? onLine, CancellationToken ct, TimeSpan? timeout)
    {
        var psi = new ProcessStartInfo(YtDlpPath)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        var error = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (onLine != null) onLine(line);
                else output.AppendLine(line);
            }
        });
        var readErr = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null) lock (error) error.AppendLine(line);
        });
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await Task.WhenAll(readOut, readErr);
        return (p.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>"ERROR: [youtube] abc: Video unavailable" → "Video unavailable".</summary>
    static string CleanError(string error)
    {
        var line = error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:")) ??
                   error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]+:\s)?", "");
        return line.Length > 0 ? line : Loc.T("VideoFailed");
    }
}
