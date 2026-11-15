using System.Diagnostics;

namespace LocalFM.Vibe;

/// <summary>Monotonic microseconds on this machine (not wall-clock: it never jumps).</summary>
public static class VibeClock
{
    private static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;
    public static long NowUs => (long)(Stopwatch.GetTimestamp() * UsPerTick);
}
