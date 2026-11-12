using System.Diagnostics;

namespace SwiftGet.Core;

/// <summary>Global token bucket shared by every connection. 0 = unlimited.</summary>
public sealed class SpeedLimiter
{
    readonly object _lock = new();
    double _tokens;
    long _last = Stopwatch.GetTimestamp();

    public long BytesPerSecond { get; set; }

    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            var limit = BytesPerSecond;
            if (limit <= 0) return;

            TimeSpan wait;
            lock (_lock)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _last) / (double)Stopwatch.Frequency;
                _last = now;
                // Small bucket (a quarter second) so the limit holds from the first second.
                var capacity = Math.Max(limit / 4, 64 * 1024);
                _tokens = Math.Min(capacity, _tokens + elapsed * limit);
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds((bytes - _tokens) / limit);
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : wait, ct);
        }
    }
}
