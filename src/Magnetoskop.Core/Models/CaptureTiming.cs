using System.Diagnostics;

namespace Magnetoskop.Core.Models;

/// <summary>
/// Converts the process-wide high-resolution performance counter to a normalized
/// 100-nanosecond monotonic timebase. Values from every capture service can be
/// compared directly; they are not UTC timestamps.
/// </summary>
public static class CaptureMonotonicClock
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>Current performance-counter position in normalized 100 ns units.</summary>
    public static long GetTimestamp100ns()
        => NormalizeQpcTimestamp(Stopwatch.GetTimestamp());

    /// <summary>Converts a raw <see cref="Stopwatch.GetTimestamp"/> value to 100 ns units.</summary>
    public static long NormalizeQpcTimestamp(long qpcTimestamp)
        => (long)((Int128)qpcTimestamp * TicksPerSecond / Stopwatch.Frequency);

    public static TimeSpan ToTimeSpan(long timestamp100ns) => TimeSpan.FromTicks(timestamp100ns);
}
