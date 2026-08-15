using Magnetoskop.Core.Models;

namespace Magnetoskop.App.Services;

/// <summary>
/// A timecode observation and the recorder state that was known when it arrived.
/// All timestamps are on the process-wide monotonic 100 ns capture clock.
/// </summary>
internal sealed record VtrFrameAssociation
{
    public required Timecode Value { get; init; }
    public required TimecodeSource Source { get; init; }
    public required long ReceivedTimestamp100ns { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required long StatusTimestamp100ns { get; init; }
    public required TransportState Transport { get; init; }
    public required bool ServoLocked { get; init; }
    public required bool TapeReverse { get; init; }

    public TimeSpan AgeAt(long captureTimestamp100ns)
        => TimeSpan.FromTicks(Math.Max(0, captureTimestamp100ns - ReceivedTimestamp100ns));

    public ObservationFreshness FreshnessAt(long captureTimestamp100ns)
    {
        var age = AgeAt(captureTimestamp100ns);
        if (age <= TimeInformation.DefaultStaleAfter) return ObservationFreshness.Fresh;
        if (age <= TimeInformation.DefaultLostAfter) return ObservationFreshness.Stale;
        return ObservationFreshness.Lost;
    }

    public bool IsServoStateFreshAt(long captureTimestamp100ns)
        => captureTimestamp100ns >= StatusTimestamp100ns
           && captureTimestamp100ns - StatusTimestamp100ns <= TimeInformation.DefaultStaleAfter.Ticks;

    public bool CanInterpolateAt(long captureTimestamp100ns)
        => Source is TimecodeSource.Ltc or TimecodeSource.Vitc
           && FreshnessAt(captureTimestamp100ns) == ObservationFreshness.Fresh
           && Transport == TransportState.Playing
           && !TapeReverse
           && ServoLocked
           && IsServoStateFreshAt(captureTimestamp100ns);
}

/// <summary>
/// Keeps a short, deduplicated history so a committed frame can be associated with
/// the nearest observation that actually preceded capture. A later VTR poll can
/// therefore never be attached retroactively to an earlier frame.
/// </summary>
internal sealed class VtrObservationHistory
{
    private const int MaxEntries = 512;
    private readonly object _gate = new();
    private readonly List<VtrFrameAssociation> _entries = new(MaxEntries);
    private readonly Dictionary<TimecodeSource, long> _lastTimestampBySource = new();

    public void Observe(
        TimeInformation information,
        VtrStatus status,
        DateTimeOffset? observedAt = null,
        long? observedTimestamp100ns = null)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(status);

        var now = observedAt ?? DateTimeOffset.UtcNow;
        var now100ns = observedTimestamp100ns ?? CaptureMonotonicClock.GetTimestamp100ns();
        var statusTimestamp100ns = status.Timestamp100ns > 0
            ? status.Timestamp100ns
            : EstimateMonotonicTimestamp(status.Timestamp, now, now100ns);

        lock (_gate)
        {
            Add_NoLock(information.CtlObservation, status, statusTimestamp100ns, now, now100ns);
            Add_NoLock(information.LtcObservation, status, statusTimestamp100ns, now, now100ns);
            Add_NoLock(information.VitcObservation, status, statusTimestamp100ns, now, now100ns);

            if (_entries.Count > MaxEntries)
            {
                var remove = _entries.Count - MaxEntries;
                _entries.RemoveRange(0, remove);
            }
        }
    }

    public VtrFrameAssociation? FindNearestPreceding(long captureTimestamp100ns)
    {
        lock (_gate)
        {
            VtrFrameAssociation? nearestAny = null;
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var candidate = _entries[index];
                if (candidate.ReceivedTimestamp100ns > captureTimestamp100ns)
                    continue;

                nearestAny ??= candidate;
                // CTL is polled immediately after the deck's best LTC/VITC value.
                // Blindly taking the newest register would therefore select CTL for
                // almost every frame and suppress valid interpolation. Prefer the
                // newest still-fresh tape timecode; use CTL only as the fallback.
                if (candidate.Source is not (TimecodeSource.Ctl or TimecodeSource.Ctl2)
                    && candidate.FreshnessAt(captureTimestamp100ns)
                        == ObservationFreshness.Fresh)
                {
                    return candidate;
                }
            }

            return nearestAny;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lastTimestampBySource.Clear();
        }
    }

    private void Add_NoLock(
        TimeObservation<Timecode>? observation,
        VtrStatus status,
        long statusTimestamp100ns,
        DateTimeOffset now,
        long now100ns)
    {
        if (observation is not { } value) return;

        var timestamp100ns = value.HasMonotonicTimestamp
            ? value.ReceivedTimestamp100ns
            : EstimateMonotonicTimestamp(value.ReceivedAt, now, now100ns);
        if (timestamp100ns <= 0) return;
        if (_lastTimestampBySource.TryGetValue(value.Source, out var previous)
            && timestamp100ns <= previous)
        {
            return;
        }

        _lastTimestampBySource[value.Source] = timestamp100ns;
        _entries.Add(new VtrFrameAssociation
        {
            Value = value.Value,
            Source = value.Source,
            ReceivedTimestamp100ns = timestamp100ns,
            ReceivedAt = value.ReceivedAt,
            StatusTimestamp100ns = statusTimestamp100ns,
            Transport = status.Transport,
            ServoLocked = status.ServoLock,
            TapeReverse = status.TapeReverse,
        });
        _entries.Sort(static (left, right) =>
            left.ReceivedTimestamp100ns.CompareTo(right.ReceivedTimestamp100ns));
    }

    private static long EstimateMonotonicTimestamp(
        DateTimeOffset receivedAt,
        DateTimeOffset now,
        long now100ns)
    {
        var ageTicks = Math.Max(0, (now - receivedAt).Ticks);
        return Math.Max(1, now100ns - ageTicks);
    }
}
