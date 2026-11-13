using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SwiftGet.Core;

public sealed record BridgeRequest(string Url, string? Referrer, string? Cookies, string? UserAgent, string? FileName);

/// <summary>
/// Tiny HTTP endpoint on 127.0.0.1 used by the browser extension.
/// GET /ping  -> health check;  POST /add {url, referrer, cookies, userAgent, fileName}.
/// Requests from web pages are refused (only extension origins or no Origin header are accepted).
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    const int MaxRequest = 1 << 20;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();

    public event Action<BridgeRequest>? DownloadRequested;

    /// <summary>Last time the browser extension contacted the app (it checks in about once a minute).</summary>
    public static DateTime? LastExtensionContact { get; private set; }
    public int Port { get; }

    public BrowserBridge(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var ct = timeout.Token;
                var stream = client.GetStream();
                var buf = new MemoryStream();
                var chunk = new byte[8192];

                int headerEnd;
                while ((headerEnd = FindHeaderEnd(buf)) < 0)
                {
                    int n = await stream.ReadAsync(chunk, ct);
                    if (n == 0 || buf.Length + n > MaxRequest) return;
                    buf.Write(chunk, 0, n);
                }

                var lines = Encoding.Latin1.GetString(buf.GetBuffer(), 0, headerEnd).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 2) return;
                string method = first[0], path = first[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int i = line.IndexOf(':');
                    if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
                }

                if (headers.TryGetValue("Origin", out var extOrigin) && IsExtensionOrigin(extOrigin))
                    LastExtensionContact = DateTime.Now;

                if (headers.TryGetValue("Origin", out var origin) && !IsExtensionOrigin(origin))
                {
                    await RespondAsync(stream, 403, "Forbidden", "{\"error\":\"forbidden\"}", ct);
                    return;
                }

                int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
                if (length < 0 || length > MaxRequest) return;
                int bodyStart = headerEnd + 4;
                while (buf.Length - bodyStart < length)
                {
                    int n = await stream.ReadAsync(chunk, ct);
                    if (n == 0) return;
                    buf.Write(chunk, 0, n);
                }
                var body = Encoding.UTF8.GetString(buf.GetBuffer(), bodyStart, length);

                if (method == "GET" && path == "/ping")
                {
                    await RespondAsync(stream, 200, "OK", "{\"app\":\"SwiftGet\",\"version\":1}", ct);
                    return;
                }

                if (method == "POST" && path == "/add")
                {
                    BridgeRequest? req = null;
                    try { req = JsonSerializer.Deserialize<BridgeRequest>(body, Json); } catch { }
                    if (req == null || !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        await RespondAsync(stream, 400, "Bad Request", "{\"error\":\"invalid url\"}", ct);
                        return;
                    }
                    await RespondAsync(stream, 200, "OK", "{\"ok\":true}", ct);
                    DownloadRequested?.Invoke(req);
                    return;
                }

                await RespondAsync(stream, 404, "Not Found", "{\"error\":\"not found\"}", ct);
            }
            catch
            {
                // malformed request or client went away
            }
        }
    }

    static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);

    static int FindHeaderEnd(MemoryStream ms)
    {
        var b = ms.GetBuffer();
        for (int i = 0; i + 3 < ms.Length; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n')
                return i;
        return -1;
    }

    static async Task RespondAsync(NetworkStream stream, int code, string reason, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
