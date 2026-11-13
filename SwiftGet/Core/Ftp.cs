using System.Net;

namespace SwiftGet.Core;

#pragma warning disable SYSLIB0014 // FtpWebRequest is the only FTP client in .NET; it still works fine

/// <summary>FTP support: size probe and reading from an offset (REST), so FTP files get resume and multiple connections too.</summary>
public static class Ftp
{
    public static bool IsFtp(string url) => url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    static FtpWebRequest Create(string url, string method)
    {
        var uri = new Uri(url);
        var req = (FtpWebRequest)WebRequest.Create(uri);
        req.Method = method;
        req.UseBinary = true;
        req.UsePassive = true;
        req.KeepAlive = false;
        req.Proxy = null;
        req.Timeout = 30000;
        req.ReadWriteTimeout = 30000;
        req.Credentials = Http.CredentialsFor(uri) ?? new NetworkCredential("anonymous", "swiftget@");
        return req;
    }

    public static async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        long size = -1;
        var req = Create(url, WebRequestMethods.Ftp.GetFileSize);
        using (ct.Register(req.Abort))
        using (var resp = (FtpWebResponse)await req.GetResponseAsync())
            size = resp.ContentLength;

        DateTimeOffset? modified = null;
        try
        {
            var req2 = Create(url, WebRequestMethods.Ftp.GetDateTimestamp);
            using (ct.Register(req2.Abort))
            using (var resp2 = (FtpWebResponse)await req2.GetResponseAsync())
                modified = resp2.LastModified;
        }
        catch { /* MDTM isn't supported by every server */ }

        var name = Format.FileNameFromUrl(url);
        return new ProbeResult(name, size, size > 0, null, LastModified: modified);
    }

    /// <summary>Opens the file at <paramref name="offset"/>. Dispose the result to close the connection.</summary>
    public static async Task<(Stream Stream, IDisposable Owner)> OpenAsync(string url, long offset, CancellationToken ct)
    {
        var req = Create(url, WebRequestMethods.Ftp.DownloadFile);
        req.ContentOffset = offset;
        var reg = ct.Register(req.Abort);
        var resp = (FtpWebResponse)await req.GetResponseAsync();
        return (resp.GetResponseStream(), new Owner(resp, reg, req));
    }

    sealed class Owner(FtpWebResponse resp, CancellationTokenRegistration reg, FtpWebRequest req) : IDisposable
    {
        public void Dispose()
        {
            reg.Dispose();
            // Abort instead of a polite close: we may stop reading before the end of the file (segment finished).
            try { req.Abort(); } catch { }
            try { resp.Dispose(); } catch { }
        }
    }
}
