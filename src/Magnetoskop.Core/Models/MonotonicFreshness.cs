namespace Magnetoskop.Core.Models;

/// <summary>Freshness classification for values on the process-wide 100 ns clock.</summary>
public static class MonotonicFreshness
{
    public static ObservationFreshness Classify(
        long? receivedTimestamp100ns,
        long nowTimestamp100ns,
        TimeSpan staleAfter,
        TimeSpan lostAfter)
    {
        if (receivedTimestamp100ns is null or <= 0)
        {
            return ObservationFreshness.Unavailable;
        }
        if (staleAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }
        if (lostAfter <= staleAfter)
        {
            throw new ArgumentOutOfRangeException(nameof(lostAfter));
        }

        var age100ns = Math.Max(0, nowTimestamp100ns - receivedTimestamp100ns.Value);
        if (age100ns <= staleAfter.Ticks) return ObservationFreshness.Fresh;
        if (age100ns <= lostAfter.Ticks) return ObservationFreshness.Stale;
        return ObservationFreshness.Lost;
    }
}
