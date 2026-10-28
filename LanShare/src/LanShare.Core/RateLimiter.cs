using System.Diagnostics;

namespace LanShare.Core;

/// <summary>
/// Shared outgoing bandwidth limiter (token bucket). One instance can be shared by every stream of every
/// session; <see cref="BytesPerSecond"/> can be changed live and waiters re-check it at least every 250 ms,
/// so lowering, raising or removing the limit takes effect almost immediately. 0 = unlimited.
/// </summary>
public sealed class RateLimiter
{
    readonly object _gate = new();
    long _bytesPerSec;
    double _tokens;
    long _last;

    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSec);
        set => Interlocked.Exchange(ref _bytesPerSec, Math.Max(0, value));
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            long limit = BytesPerSecond;
            if (limit <= 0)
                return;

            double waitSec;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                double capacity = Math.Max(limit, bytes);
                _tokens =
                    _last == 0
                        ? capacity
                        : Math.Min(
                            capacity,
                            _tokens + (now - _last) / (double)Stopwatch.Frequency * limit
                        );
                _last = now;
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                waitSec = (bytes - _tokens) / limit;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(waitSec, 0.25)), ct);
        }
    }
}
