using System.Net;

namespace SwiftGet.Core;

/// <summary>Shared HTTP client; rebuilt when proxy or login settings change.</summary>
public static class Http
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    static List<SiteLogin> _logins = new();

    public static HttpClient Client { get; private set; } = Build(null);

    /// <summary>Applies proxy and site-login settings. Running downloads keep the client they started with.</summary>
    public static void Configure(AppSettings settings)
    {
        _logins = settings.SiteLogins.ToList();
        Client = Build(settings);
    }

    static HttpClient Build(AppSettings? s)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            Credentials = new SiteCredentials(),
            PreAuthenticate = true,
        };
        switch (s?.ProxyMode)
        {
            case "None":
                handler.UseProxy = false;
                break;
            case "Manual" when !string.IsNullOrWhiteSpace(s.ProxyHost):
                var proxy = new WebProxy(s.ProxyHost.Trim(), s.ProxyPort);
                if (!string.IsNullOrWhiteSpace(s.ProxyUser))
                    proxy.Credentials = new NetworkCredential(s.ProxyUser, Secret.Unprotect(s.ProxyPassword));
                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;
            // "System": SocketsHttpHandler uses the Windows proxy settings by default
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The saved login for a URL, from Options → Connection → Site logins, or from user:pass@ in the URL.</summary>
    public static NetworkCredential? CredentialsFor(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
            return new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : "");
        }
        var login = _logins.FirstOrDefault(l => l.Matches(uri));
        return login == null ? null : new NetworkCredential(login.User, Secret.Unprotect(login.Password));
    }

    sealed class SiteCredentials : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) => CredentialsFor(uri);
    }

    public static HttpRequestMessage CreateRequest(string url, string? referrer, string? cookies, string? userAgent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var r))
            req.Headers.Referrer = r;
        if (!string.IsNullOrWhiteSpace(cookies))
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
        return req;
    }

    /// <summary>Downloads a small text resource (playlists, HTML pages).</summary>
    public static async Task<(string Text, Uri FinalUri, string? ContentType)> GetTextAsync(
        string url, string? referrer, string? cookies, string? userAgent, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var req = CreateRequest(url, referrer, cookies, userAgent);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(Loc.F("ErrServer", (int)resp.StatusCode, resp.ReasonPhrase));
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        return (text, resp.RequestMessage?.RequestUri ?? new Uri(url), resp.Content.Headers.ContentType?.MediaType);
    }

    public static async Task<byte[]> GetBytesAsync(Uri uri, string? referrer, string? cookies, string? userAgent,
        long? rangeStart, long? rangeLength, CancellationToken ct)
    {
        using var req = CreateRequest(uri.ToString(), referrer, cookies, userAgent);
        if (rangeStart is long start)
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, rangeLength is long len ? start + len - 1 : null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(Loc.F("ErrServer", (int)resp.StatusCode, resp.ReasonPhrase));
        return await resp.Content.ReadAsByteArrayAsync(cts.Token);
    }
}
