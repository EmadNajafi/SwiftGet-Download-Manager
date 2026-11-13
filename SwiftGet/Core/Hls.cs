using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SwiftGet.Models;

namespace SwiftGet.Core;

/// <summary>A media playlist: the parts of one HLS stream.</summary>
public sealed record HlsPlaylist(List<HlsPart> Parts, HlsPart? Init, Uri? AudioPlaylist);

public sealed record HlsPart(int Index, Uri Uri, long? RangeStart, long? RangeLength, Uri? KeyUri, byte[]? Iv, long Sequence);

/// <summary>Reads HLS (m3u8) playlists: picks the best quality, follows byte ranges and AES-128 keys.</summary>
public static partial class HlsParser
{
    public static bool LooksLikeStream(string url, string? contentType) =>
        (contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false) ||
        Regex.IsMatch(url, @"\.m3u8(\?|#|$)", RegexOptions.IgnoreCase);

    [GeneratedRegex("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)")]
    private static partial Regex AttrRegex();

    static Dictionary<string, string> Attrs(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int colon = line.IndexOf(':');
        if (colon < 0) return d;
        foreach (Match m in AttrRegex().Matches(line[(colon + 1)..]))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    /// <summary>Loads a playlist; for a master playlist, follows the highest-bandwidth variant.</summary>
    public static async Task<HlsPlaylist> LoadAsync(DownloadItem item, CancellationToken ct)
    {
        var (text, baseUri, _) = await Http.GetTextAsync(item.Url, item.Referrer, item.Cookies, item.UserAgent, ct);
        if (!text.TrimStart().StartsWith("#EXTM3U")) throw new InvalidDataException(Loc.T("ErrNotPlaylist"));

        if (text.Contains("#EXT-X-STREAM-INF"))
        {
            var lines = text.Split('\n').Select(l => l.Trim()).ToList();
            var audioGroups = lines.Where(l => l.StartsWith("#EXT-X-MEDIA:"))
                .Select(Attrs)
                .Where(a => a.GetValueOrDefault("TYPE") == "AUDIO" && a.ContainsKey("URI"))
                .ToList();

            (long Bandwidth, Uri Uri, string? Audio)? best = null;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF")) continue;
                var a = Attrs(lines[i]);
                var uriLine = lines.Skip(i + 1).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                if (uriLine == null) continue;
                long bw = long.TryParse(a.GetValueOrDefault("BANDWIDTH"), out var b) ? b : 0;
                if (best == null || bw > best.Value.Bandwidth)
                    best = (bw, new Uri(baseUri, uriLine), a.GetValueOrDefault("AUDIO"));
            }
            if (best == null) throw new InvalidDataException(Loc.T("ErrNotPlaylist"));

            Uri? audio = null;
            if (best.Value.Audio is { } group)
            {
                var rendition = audioGroups.FirstOrDefault(a => a.GetValueOrDefault("GROUP-ID") == group && a.GetValueOrDefault("DEFAULT") == "YES")
                             ?? audioGroups.FirstOrDefault(a => a.GetValueOrDefault("GROUP-ID") == group);
                if (rendition != null) audio = new Uri(baseUri, rendition["URI"]);
            }

            var media = await LoadMediaAsync(best.Value.Uri.ToString(), item, ct);
            return media with { AudioPlaylist = audio };
        }
        return Parse(text, baseUri);
    }

    public static async Task<HlsPlaylist> LoadMediaAsync(string url, DownloadItem item, CancellationToken ct)
    {
        var (text, baseUri, _) = await Http.GetTextAsync(url, item.Referrer, item.Cookies, item.UserAgent, ct);
        return Parse(text, baseUri);
    }

    static HlsPlaylist Parse(string text, Uri baseUri)
    {
        var parts = new List<HlsPart>();
        HlsPart? init = null;
        Uri? keyUri = null;
        byte[]? iv = null;
        long sequence = 0, nextRangeStart = 0;
        long? rangeStart = null, rangeLength = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
                long.TryParse(line[22..], out sequence);
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var a = Attrs(line);
                var method = a.GetValueOrDefault("METHOD", "NONE");
                if (method == "NONE") { keyUri = null; iv = null; }
                else if (method == "AES-128")
                {
                    keyUri = new Uri(baseUri, a["URI"]);
                    iv = a.TryGetValue("IV", out var hex) ? Convert.FromHexString(hex.Replace("0x", "", StringComparison.OrdinalIgnoreCase).PadLeft(32, '0')) : null;
                }
                else throw new NotSupportedException(Loc.F("ErrDrm", method));
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var a = Attrs(line);
                long? s = null, l = null;
                if (a.TryGetValue("BYTERANGE", out var br)) (l, s) = ParseRange(br, 0);
                init = new HlsPart(-1, new Uri(baseUri, a["URI"]), s, l, null, null, 0);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                var (len, start) = ParseRange(line[17..], nextRangeStart);
                rangeLength = len;
                rangeStart = start;
                nextRangeStart = start + len;
            }
            else if (!line.StartsWith('#'))
            {
                parts.Add(new HlsPart(parts.Count, new Uri(baseUri, line), rangeStart, rangeLength, keyUri, iv, sequence));
                sequence++;
                rangeStart = rangeLength = null;
            }
        }
        if (parts.Count == 0) throw new InvalidDataException(Loc.T("ErrNotPlaylist"));
        return new HlsPlaylist(parts, init, null);
    }

    static (long Length, long Start) ParseRange(string value, long defaultStart)
    {
        var p = value.Split('@');
        long len = long.Parse(p[0], CultureInfo.InvariantCulture);
        long start = p.Length > 1 ? long.Parse(p[1], CultureInfo.InvariantCulture) : defaultStart;
        return (len, start);
    }
}

/// <summary>Downloads an HLS stream: parts in parallel, decrypted, joined into one file (optionally made MP4 with ffmpeg).</summary>
public sealed class HlsDownloader
{
    readonly DownloadItem _item;
    readonly AppSettings _settings;
    readonly SpeedLimiter _limiter;
    readonly Dictionary<Uri, byte[]> _keys = new();

    public HlsDownloader(DownloadItem item, AppSettings settings, SpeedLimiter limiter)
    {
        _item = item;
        _settings = settings;
        _limiter = limiter;
    }

    /// <summary>Downloads everything and returns the finished file and its extension (".mp4" or ".ts").</summary>
    public async Task<(string Path, string Extension)> RunAsync(CancellationToken ct)
    {
        var playlist = await HlsParser.LoadAsync(_item, ct);
        var dir = _item.StreamPartsDir;
        var info = Directory.CreateDirectory(dir);
        info.Attributes |= FileAttributes.Hidden;

        HlsPlaylist? audio = null;
        var ffmpeg = Ffmpeg.Find(_settings);
        if (playlist.AudioPlaylist != null && ffmpeg != null)
            audio = await HlsParser.LoadMediaAsync(playlist.AudioPlaylist.ToString(), _item, ct);

        var jobs = playlist.Parts.Select(p => (Part: p, File: Path.Combine(dir, $"v{p.Index:D6}.part"))).ToList();
        if (audio != null) jobs.AddRange(audio.Parts.Select(p => (Part: p, File: Path.Combine(dir, $"a{p.Index:D6}.part"))));
        if (playlist.Init != null) jobs.Insert(0, (playlist.Init, Path.Combine(dir, "vinit.part")));
        if (audio?.Init != null) jobs.Insert(0, (audio.Init, Path.Combine(dir, "ainit.part")));

        lock (_item.SyncRoot)
        {
            _item.StreamTotal = jobs.Count;
            _item.StreamDone = jobs.Count(j => File.Exists(j.File));
            _item.StreamBytes = jobs.Where(j => File.Exists(j.File)).Sum(j => new FileInfo(j.File).Length);
        }

        var queue = new Queue<(HlsPart Part, string File)>(jobs.Where(j => !File.Exists(j.File)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int workers = Math.Clamp(_settings.Connections, 1, 16);
        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            while (true)
            {
                (HlsPart Part, string File) job;
                lock (queue)
                {
                    if (queue.Count == 0) return;
                    job = queue.Dequeue();
                }
                lock (_item.SyncRoot) _item.StreamActive++;
                try { await FetchWithRetryAsync(job.Part, job.File, linked.Token); }
                catch { linked.Cancel(); throw; }
                finally { lock (_item.SyncRoot) _item.StreamActive--; }
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            var real = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).FirstOrDefault(e => e is not OperationCanceledException);
            if (real != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(real);
            throw;
        }

        // join the parts
        bool fmp4 = playlist.Init != null;
        var video = Path.Combine(dir, fmp4 ? "video.mp4" : "video.ts");
        Join(jobs.Where(j => Path.GetFileName(j.File).StartsWith('v')).Select(j => j.File), video);

        string result = video, ext = fmp4 ? ".mp4" : ".ts";
        if (audio != null && ffmpeg != null)
        {
            var audioFile = Path.Combine(dir, audio.Init != null ? "audio.mp4" : "audio.ts");
            Join(jobs.Where(j => Path.GetFileName(j.File).StartsWith('a')).Select(j => j.File), audioFile);
            var muxed = Path.Combine(dir, "muxed.mp4");
            if (await Ffmpeg.RunAsync(ffmpeg, $"-y -i \"{video}\" -i \"{audioFile}\" -map 0:v -map 1:a -c copy \"{muxed}\"", ct))
                (result, ext) = (muxed, ".mp4");
        }
        else if (!fmp4 && ffmpeg != null)
        {
            var mp4 = Path.Combine(dir, "converted.mp4");
            if (await Ffmpeg.RunAsync(ffmpeg, $"-y -i \"{video}\" -c copy -bsf:a aac_adtstoasc \"{mp4}\"", ct))
                (result, ext) = (mp4, ".mp4");
        }
        return (result, ext);
    }

    static void Join(IEnumerable<string> files, string output)
    {
        using var outStream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        foreach (var f in files)
            using (var input = File.OpenRead(f)) input.CopyTo(outStream);
    }

    async Task FetchWithRetryAsync(HlsPart part, string file, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var data = await Http.GetBytesAsync(part.Uri, _item.Referrer, _item.Cookies, _item.UserAgent, part.RangeStart, part.RangeLength, ct);
                await _limiter.ConsumeAsync(data.Length, ct);
                if (part.KeyUri != null) data = Decrypt(data, await KeyAsync(part.KeyUri, ct), part.Iv ?? SequenceIv(part.Sequence));
                var tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, data, ct);
                File.Move(tmp, file, overwrite: true);
                lock (_item.SyncRoot)
                {
                    _item.StreamDone++;
                    _item.StreamBytes += data.Length;
                    // estimate the final size from the average part size
                    _item.TotalSize = _item.StreamDone > 0 ? _item.StreamBytes / _item.StreamDone * _item.StreamTotal : -1;
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch when (attempt < _settings.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(20, 2 * (attempt + 1))), ct);
            }
        }
    }

    async Task<byte[]> KeyAsync(Uri uri, CancellationToken ct)
    {
        lock (_keys) if (_keys.TryGetValue(uri, out var k)) return k;
        var key = await Http.GetBytesAsync(uri, _item.Referrer, _item.Cookies, _item.UserAgent, null, null, ct);
        lock (_keys) _keys[uri] = key;
        return key;
    }

    static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 15; i >= 8; i--, sequence >>= 8) iv[i] = (byte)sequence;
        return iv;
    }

    static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }
}

/// <summary>Optional ffmpeg, used only to turn .ts streams into .mp4 and to join separate audio.</summary>
public static class Ffmpeg
{
    public static string? Find(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.FfmpegPath) && File.Exists(s.FfmpegPath)) return s.FfmpegPath;
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static async Task<bool> RunAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            })!;
            _ = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
