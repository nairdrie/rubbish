using System;
using System.Collections.Generic;

/// <summary>
/// Per-client rate and size limit for one named message or RPC on the host (D12): a token bucket of <c>burst</c> messages,
/// refilled at <c>perSecond</c>, and a hard <c>maxBytes</c> per message. Main thread only. Pass a monotonic clock
/// (<c>Time.realtimeSinceStartupAsDouble</c> or <c>NetworkManager.RealTimeProvider</c>).
/// </summary>
public sealed class NetGuard
{
    struct Bucket { public double tokens, last; }

    readonly float perSecond;
    readonly int burst, maxBytes;
    readonly Dictionary<ulong, Bucket> buckets = new Dictionary<ulong, Bucket>();

    public NetGuard(float perSecond, int burst, int maxBytes)
    {
        if (perSecond <= 0f) throw new ArgumentOutOfRangeException(nameof(perSecond));
        if (burst < 1) throw new ArgumentOutOfRangeException(nameof(burst));
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.perSecond = perSecond;
        this.burst = burst;
        this.maxBytes = maxBytes;
    }

    /// <summary>Messages refused since construction (any client).</summary>
    public int Refused { get; private set; }

    public bool Allow(ulong clientId, int bytes, double nowSeconds)
    {
        if (bytes < 0 || bytes > maxBytes) { Refused++; return false; }
        if (!buckets.TryGetValue(clientId, out var b)) b = new Bucket { tokens = burst, last = nowSeconds };
        double dt = nowSeconds - b.last;
        if (dt > 0) b.tokens = Math.Min(burst, b.tokens + dt * perSecond);
        b.last = Math.Max(b.last, nowSeconds); // a clock that goes backwards never mints tokens
        bool ok = b.tokens >= 1.0;
        if (ok) b.tokens -= 1.0;
        else Refused++;
        buckets[clientId] = b;
        return ok;
    }

    public void Forget(ulong clientId) => buckets.Remove(clientId);
}
