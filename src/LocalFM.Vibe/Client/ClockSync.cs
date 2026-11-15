namespace LocalFM.Vibe.Client;

/// <summary>
/// Estimates the host's clock from ping/pong round trips (NTP-style). The offset of the fastest of the
/// recent round trips wins: queueing delay only ever makes a round trip slower and its estimate worse.
/// </summary>
public sealed class ClockSync
{
    private const int Keep = 16;
    private readonly Lock _gate = new();
    private readonly Queue<(long Offset, long Rtt)> _samples = new();
    private long _offset;

    /// <summary>Host clock minus ours, in µs.</summary>
    public long OffsetUs { get { lock (_gate) return _offset; } }
    public long BestRttUs { get; private set; } = long.MaxValue;
    public int Samples { get { lock (_gate) return _samples.Count; } }
    public bool IsSynced => Samples >= 3;

    /// <param name="t0">Ours, when the ping left.</param>
    /// <param name="t1">The host's, when it answered.</param>
    /// <param name="t2">Ours, when the pong arrived.</param>
    public void Add(long t0, long t1, long t2)
    {
        long rtt = t2 - t0;
        if (rtt < 0) return;
        long offset = t1 - (t0 + t2) / 2;
        lock (_gate)
        {
            _samples.Enqueue((offset, rtt));
            while (_samples.Count > Keep) _samples.Dequeue();
            var best = _samples.MinBy(s => s.Rtt);
            _offset = best.Offset;
            BestRttUs = best.Rtt;
        }
    }

    public long ToHost(long localUs) => localUs + OffsetUs;
    public long ToLocal(long hostUs) => hostUs - OffsetUs;
}
