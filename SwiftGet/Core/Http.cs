using System.Net;

namespace SwiftGet.Core;

public static class Http
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    // One shared client: connection pooling, system proxy, no automatic cookies (we send the browser's).
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        MaxConnectionsPerServer = 64,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

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
}
