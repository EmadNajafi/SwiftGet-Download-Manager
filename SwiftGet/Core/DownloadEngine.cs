using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using SwiftGet.Models;

namespace SwiftGet.Core;

public sealed record ProbeResult(string FileName, long Size, bool ResumeSupported, string? ContentType,
    bool IsStream = false, DateTimeOffset? LastModified = null, string? ETag = null);

/// <summary>
/// Downloads one item. When the server supports byte ranges the file is split into segments that are
/// fetched in parallel; when a connection finishes early it takes half of the largest remaining segment
/// (dynamic segmentation), so all connections stay busy until the end.
/// </summary>
public sealed class DownloadEngine
{
    const int BufferSize = 64 * 1024;
    const long MinSplitSize = 1024 * 1024;
    static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(30);

    readonly DownloadItem _item;
    readonly AppSettings _settings;
    readonly SpeedLimiter _limiter;
    readonly CancellationTokenSource _cts = new();

    public DownloadItem Item => _item;
    public Task RunningTask { get; private set; } = Task.CompletedTask;
    public event Action<DownloadEngine>? Finished;

    public DownloadEngine(DownloadItem item, AppSettings settings, SpeedLimiter limiter)
    {
        _item = item;
        _settings = settings;
        _limiter = limiter;
    }

    public void Start() => RunningTask = Task.Run(RunAsync);

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ probe

    public static async Task<ProbeResult> ProbeAsync(string url, string? referrer, string? cookies, string? userAgent, CancellationToken ct)
    {
        if (Ftp.IsFtp(url)) return await Ftp.ProbeAsync(url, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IoTimeout);
        using var req = Http.CreateRequest(url, referrer, cookies, userAgent);
        req.Headers.Range = new RangeHeaderValue(0, null);
        using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(Loc.F("ErrServer", (int)resp.StatusCode, resp.ReasonPhrase));

        var h = resp.Content.Headers;
        long size = -1;
        bool ranges = false;
        if (resp.StatusCode == HttpStatusCode.PartialContent && h.ContentRange?.Length is long total)
        {
            size = total;
            ranges = true;
        }
        else if (h.ContentLength is long len)
        {
            size = len;
        }

        string? name = h.ContentDisposition?.FileNameStar ?? h.ContentDisposition?.FileName;
        name = name?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = Format.FileNameFromUrl((resp.RequestMessage?.RequestUri ?? new Uri(url)).ToString());

        var type = h.ContentType?.MediaType;
        if (HlsParser.LooksLikeStream(url, type))
        {
            // a video stream (playlist of parts); the final file becomes .ts or .mp4
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(stem) || stem is "index" or "master" or "playlist" or "chunklist") stem = "video";
            return new ProbeResult(Format.SanitizeFileName(stem + ".mp4"), -1, true, type, IsStream: true);
        }

        return new ProbeResult(Format.SanitizeFileName(name), size, ranges, type,
            LastModified: h.LastModified, ETag: resp.Headers.ETag?.Tag);
    }

    // ------------------------------------------------------------------ main flow

    async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            _item.ErrorMessage = null;
            _item.Status = DownloadStatus.Connecting;

            var probe = await ProbeAsync(_item.Url, _item.Referrer, _item.Cookies, _item.UserAgent, ct);
            bool fresh;
            lock (_item.SyncRoot) fresh = _item.Segments.Count == 0;
            if (_item.AutoFileName && fresh) ApplyServerFileName(probe.FileName);
            Directory.CreateDirectory(_item.SaveFolder);

            bool segmented = probe.ResumeSupported && probe.Size > 0;
            bool resume = segmented && !fresh && _item.TotalSize == probe.Size && File.Exists(_item.TempPath);
            if (!probe.IsStream && !_item.IsStream)
            {
                _item.TotalSize = probe.Size;
                _item.ResumeSupported = segmented;
                if (!resume) InitSegments(segmented);
            }

            _item.RemoteLastModified = probe.LastModified;
            _item.RemoteETag = probe.ETag;

            if (probe.IsStream || _item.IsStream)
            {
                await RunStreamAsync(ct);
                return;
            }

            _item.Status = DownloadStatus.Downloading;
            if (segmented) await DownloadSegmentedAsync(ct);
            else await DownloadSingleAsync(ct);

            var got = _item.Downloaded;
            if (_item.TotalSize > 0 && got != _item.TotalSize)
                throw new IOException(Loc.T("ErrSizeMismatch"));
            if (_item.TotalSize <= 0) _item.TotalSize = got;

            Finish(_item.TempPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            _item.ErrorMessage = Describe(ex);
            _item.Status = DownloadStatus.Error;
        }
        finally
        {
            lock (_item.SyncRoot)
                foreach (var s in _item.Segments) s.Active = false;
            Finished?.Invoke(this);
            _cts.Dispose();
        }
    }

    /// <summary>Moves the finished file into place and marks the download completed.</summary>
    void Finish(string source)
    {
        string target;
        if (_item.ReplaceExisting)
        {
            target = _item.FullPath;
            File.Move(source, target, overwrite: true);
            _item.ReplaceExisting = false;
        }
        else
        {
            target = Format.UniquePath(_item.FullPath);
            File.Move(source, target);
        }
        _item.FileName = Path.GetFileName(target);
        MarkOfTheWeb(target);
        _item.DateCompleted = DateTime.Now;
        _item.Status = DownloadStatus.Completed;
    }

    /// <summary>Records that the file came from the internet (Zone.Identifier), so Windows SmartScreen checks it like browser downloads.</summary>
    void MarkOfTheWeb(string path)
    {
        if (!_settings.MarkOfTheWeb) return;
        try
        {
            var zone = "[ZoneTransfer]\r\nZoneId=3\r\n" +
                       (string.IsNullOrEmpty(_item.Referrer) ? "" : $"ReferrerUrl={_item.Referrer}\r\n") +
                       $"HostUrl={_item.Url}\r\n";
            File.WriteAllText(path + ":Zone.Identifier", zone);
        }
        catch
        {
            // FAT32/exFAT drives can't store it
        }
    }

    async Task RunStreamAsync(CancellationToken ct)
    {
        _item.IsStream = true;
        _item.ResumeSupported = true;
        _item.Status = DownloadStatus.Downloading;
        var (path, ext) = await new HlsDownloader(_item, _settings, _limiter).RunAsync(ct);
        if (!string.Equals(Path.GetExtension(_item.FileName), ext, StringComparison.OrdinalIgnoreCase))
            _item.FileName = Path.GetFileNameWithoutExtension(_item.FileName) + ext;
        _item.TotalSize = new FileInfo(path).Length;
        Finish(path);
        try { Directory.Delete(_item.StreamPartsDir, true); } catch { }
    }

    void ApplyServerFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var oldAutoFolder = Categories.FolderFor(_settings, _item.Category);
        var cat = Categories.Detect(name);
        _item.FileName = name;
        if (cat != _item.Category)
        {
            if (string.Equals(_item.SaveFolder, oldAutoFolder, StringComparison.OrdinalIgnoreCase))
                _item.SaveFolder = Categories.FolderFor(_settings, cat);
            _item.Category = cat;
        }
        _item.AutoFileName = false;
    }

    void InitSegments(bool segmented)
    {
        var list = new List<Segment>();
        long size = _item.TotalSize;
        if (segmented)
        {
            int n = (int)Math.Clamp(size / MinSplitSize, 1, Math.Max(1, _settings.Connections));
            long part = size / n;
            for (int i = 0; i < n; i++)
            {
                long s = i * part;
                long e = i == n - 1 ? size - 1 : s + part - 1;
                list.Add(new Segment { Start = s, End = e });
            }
        }
        else
        {
            list.Add(new Segment { Start = 0, End = size > 0 ? size - 1 : -1 });
        }

        lock (_item.SyncRoot) _item.Segments = list;

        using var fs = new FileStream(_item.TempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        if (segmented) fs.SetLength(size);
    }

    // ------------------------------------------------------------------ single connection

    async Task DownloadSingleAsync(CancellationToken ct)
    {
        Segment seg;
        lock (_item.SyncRoot)
        {
            seg = _item.Segments[0];
            seg.Downloaded = 0;
            seg.Active = true;
        }

        var (net, owner) = await OpenAsync(0, null, ct);
        using (owner)
        await using (net)
        await using (var fs = new FileStream(_item.TempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, BufferSize, true))
            await CopyAsync(net, fs, seg, bounded: false, ct);
    }

    // ------------------------------------------------------------------ multi connection

    async Task DownloadSegmentedAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int workers = Math.Max(1, _settings.Connections);
        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            try { await WorkerAsync(linked.Token); }
            catch { linked.Cancel(); throw; }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            var real = tasks.Where(t => t.IsFaulted)
                            .Select(t => t.Exception!.InnerException!)
                            .FirstOrDefault(e => e is not OperationCanceledException);
            if (real != null) ExceptionDispatchInfo.Throw(real);
            throw;
        }
    }

    async Task WorkerAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var seg = TakeSegment();
            if (seg == null) return;
            try
            {
                await RunSegmentWithRetryAsync(seg, ct);
            }
            finally
            {
                lock (_item.SyncRoot) seg.Active = false;
            }
        }
    }

    /// <summary>Gets an idle unfinished segment, or splits the biggest running one in half.</summary>
    Segment? TakeSegment()
    {
        lock (_item.SyncRoot)
        {
            var idle = _item.Segments.FirstOrDefault(s => !s.Active && s.Remaining > 0);
            if (idle != null)
            {
                idle.Active = true;
                return idle;
            }

            var big = _item.Segments.Where(s => s.Active).OrderByDescending(s => s.Remaining).FirstOrDefault();
            if (big == null || big.Remaining < 2 * MinSplitSize) return null;

            long mid = big.Position + big.Remaining / 2;
            var created = new Segment { Start = mid, End = big.End, Active = true };
            big.End = mid - 1;
            _item.Segments.Add(created);
            _item.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            return created;
        }
    }

    async Task RunSegmentWithRetryAsync(Segment seg, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                await DownloadSegmentAsync(seg, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (++attempt <= _settings.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct);
            }
        }
    }

    async Task DownloadSegmentAsync(Segment seg, CancellationToken ct)
    {
        long from, to;
        lock (_item.SyncRoot)
        {
            if (seg.Remaining <= 0) return;
            from = seg.Position;
            to = seg.End;
        }

        var (net, owner) = await OpenAsync(from, to, ct);
        using (owner)
        await using (net)
        await using (var fs = new FileStream(_item.TempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1, true))
        {
            fs.Seek(from, SeekOrigin.Begin);
            await CopyAsync(net, fs, seg, bounded: true, ct);
        }
    }

    /// <summary>Opens the file from <paramref name="from"/> (to <paramref name="to"/> if given) over HTTP(S) or FTP.</summary>
    async Task<(Stream Stream, IDisposable Owner)> OpenAsync(long from, long? to, CancellationToken ct)
    {
        if (Ftp.IsFtp(_item.Url)) return await Ftp.OpenAsync(_item.Url, from, ct);

        var req = Http.CreateRequest(_item.Url, _item.Referrer, _item.Cookies, _item.UserAgent);
        if (to != null) req.Headers.Range = new RangeHeaderValue(from, to);
        var resp = await SendAsync(req, ct);
        if (to != null && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            resp.Dispose();
            req.Dispose();
            throw new IOException(Loc.F("ErrNoRange", (int)resp.StatusCode));
        }
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            var reason = resp.ReasonPhrase;
            resp.Dispose();
            req.Dispose();
            throw new HttpRequestException(Loc.F("ErrServer", code, reason));
        }
        return (await resp.Content.ReadAsStreamAsync(ct), new Both(resp, req));
    }

    sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }

    // ------------------------------------------------------------------ helpers

    static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(IoTimeout);
        var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
        headerCts.CancelAfter(Timeout.Infinite);
        return resp;
    }

    async Task CopyAsync(Stream net, FileStream fs, Segment seg, bool bounded, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            int want = BufferSize;
            if (bounded)
            {
                lock (_item.SyncRoot)
                {
                    long rem = seg.Remaining;
                    if (rem <= 0) return;
                    want = (int)Math.Min(BufferSize, rem);
                }
            }

            int read;
            readCts.CancelAfter(IoTimeout);
            try
            {
                read = await net.ReadAsync(buffer.AsMemory(0, want), readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Loc.T("ErrTimeout"));
            }
            readCts.CancelAfter(Timeout.Infinite);

            if (read == 0)
            {
                if (bounded) throw new IOException(Loc.T("ErrClosedEarly"));
                return;
            }

            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            lock (_item.SyncRoot) seg.Downloaded += read;
            await _limiter.ConsumeAsync(read, ct);
        }
    }

    static string Describe(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner }) ex = inner;
        return ex switch
        {
            HttpRequestException h when h.InnerException != null => $"{h.Message} ({h.InnerException.Message})",
            _ => ex.Message
        };
    }
}
