namespace Magnetoskop.Core.Models;

/// <summary>Identifies the source of a time value reported by the VTR.</summary>
public enum TimecodeSource
{
    /// <summary>CTL counter (tape Timer-1).</summary>
    Ctl,
    /// <summary>CTL counter (tape Timer-2).</summary>
    Ctl2,
    /// <summary>Longitudinal timecode read from tape.</summary>
    Ltc,
    /// <summary>Vertical interval timecode read from tape.</summary>
    Vitc,
    /// <summary>LTC corrected by the tape timer (returned when LTC/VITC are unreadable).</summary>
    CorrectedLtc,
    /// <summary>Held VITC value (returned when the tape is stopped).</summary>
    HoldVitc,
    /// <summary>Held LTC user-bit value.</summary>
    HoldLtc,
}

/// <summary>Age classification for a value observed from the recorder.</summary>
public enum ObservationFreshness
{
    Unavailable,
    Fresh,
    Stale,
    Lost,
}

/// <summary>A recorder value together with its source and host receipt time.</summary>
public readonly record struct TimeObservation<T>(
    T Value,
    TimecodeSource Source,
    DateTimeOffset ReceivedAt,
    long ReceivedTimestamp100ns = 0)
{
    public bool HasMonotonicTimestamp => ReceivedTimestamp100ns > 0;

    public ObservationFreshness FreshnessAt(
        DateTimeOffset now,
        TimeSpan? staleAfter = null,
        TimeSpan? lostAfter = null)
        => TimeInformation.ClassifyFreshness(
            ReceivedAt,
            now,
            staleAfter ?? TimeInformation.DefaultStaleAfter,
            lostAfter ?? TimeInformation.DefaultLostAfter);

    /// <summary>
    /// Classifies age on the shared monotonic capture clock. Falls back to the
    /// supplied wall clock only for observations created by legacy sources that do
    /// not carry a monotonic receipt timestamp.
    /// </summary>
    public ObservationFreshness FreshnessAt(
        long nowTimestamp100ns,
        DateTimeOffset now,
        TimeSpan? staleAfter = null,
        TimeSpan? lostAfter = null)
        => HasMonotonicTimestamp
            ? MonotonicFreshness.Classify(
                ReceivedTimestamp100ns,
                nowTimestamp100ns,
                staleAfter ?? TimeInformation.DefaultStaleAfter,
                lostAfter ?? TimeInformation.DefaultLostAfter)
            : FreshnessAt(now, staleAfter, lostAfter);

    public TimeSpan AgeAt(long nowTimestamp100ns, DateTimeOffset now)
        => HasMonotonicTimestamp
            ? TimeSpan.FromTicks(Math.Max(0, nowTimestamp100ns - ReceivedTimestamp100ns))
            : now - ReceivedAt;
}

/// <summary>SMPTE user bits: 8 binary groups packed into 4 bytes.</summary>
public readonly record struct UserBits(byte Byte1, byte Byte2, byte Byte3, byte Byte4)
{
    public static readonly UserBits Zero = new(0, 0, 0, 0);

    public override string ToString() => $"{Byte4:X2} {Byte3:X2} {Byte2:X2} {Byte1:X2}";
}

/// <summary>A snapshot of all time information reported by the recorder.</summary>
public sealed record TimeInformation
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultLostAfter = TimeSpan.FromSeconds(3);

    public Timecode? Ctl { get; init; }
    public Timecode? Ltc { get; init; }
    public Timecode? Vitc { get; init; }
    public UserBits? LtcUserBits { get; init; }
    public UserBits? VitcUserBits { get; init; }

    /// <summary>Independent host receipt times. A poll of one register never refreshes another.</summary>
    public DateTimeOffset? CtlReceivedAt { get; init; }
    public DateTimeOffset? LtcReceivedAt { get; init; }
    public DateTimeOffset? VitcReceivedAt { get; init; }
    public DateTimeOffset? LtcUserBitsReceivedAt { get; init; }
    public DateTimeOffset? VitcUserBitsReceivedAt { get; init; }

    /// <summary>Process-wide monotonic receipt timestamps, normalized to 100 ns units.</summary>
    public long? CtlReceivedTimestamp100ns { get; init; }
    public long? LtcReceivedTimestamp100ns { get; init; }
    public long? VitcReceivedTimestamp100ns { get; init; }
    public long? LtcUserBitsReceivedTimestamp100ns { get; init; }
    public long? VitcUserBitsReceivedTimestamp100ns { get; init; }

    /// <summary>Preserves corrected/held response kinds instead of flattening them for display.</summary>
    public TimecodeSource? CtlSource { get; init; }
    public TimecodeSource? LtcSource { get; init; }
    public TimecodeSource? VitcSource { get; init; }
    public TimecodeSource? LtcUserBitsSource { get; init; }
    public TimecodeSource? VitcUserBitsSource { get; init; }

    /// <summary>Which source produced the primary display timecode.</summary>
    public TimecodeSource? PrimarySource { get; init; }

    /// <summary>Receipt time of the most recently published part of this composite snapshot.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public TimeObservation<Timecode>? CtlObservation
        => Ctl is { } value
            ? new(value, CtlSource ?? TimecodeSource.Ctl, CtlReceivedAt ?? Timestamp,
                CtlReceivedTimestamp100ns ?? 0)
            : null;

    public TimeObservation<Timecode>? LtcObservation
        => Ltc is { } value
            ? new(value, LtcSource ?? TimecodeSource.Ltc, LtcReceivedAt ?? Timestamp,
                LtcReceivedTimestamp100ns ?? 0)
            : null;

    public TimeObservation<Timecode>? VitcObservation
        => Vitc is { } value
            ? new(value, VitcSource ?? TimecodeSource.Vitc, VitcReceivedAt ?? Timestamp,
                VitcReceivedTimestamp100ns ?? 0)
            : null;

    public TimeObservation<UserBits>? LtcUserBitsObservation
        => LtcUserBits is { } value
            ? new(value, LtcUserBitsSource ?? TimecodeSource.Ltc, LtcUserBitsReceivedAt ?? Timestamp,
                LtcUserBitsReceivedTimestamp100ns ?? 0)
            : null;

    public TimeObservation<UserBits>? VitcUserBitsObservation
        => VitcUserBits is { } value
            ? new(value, VitcUserBitsSource ?? TimecodeSource.Vitc, VitcUserBitsReceivedAt ?? Timestamp,
                VitcUserBitsReceivedTimestamp100ns ?? 0)
            : null;

    /// <summary>Newest receipt represented by any available field.</summary>
    public DateTimeOffset? LatestReceivedAt
        => new DateTimeOffset?[]
        {
            Ctl is null ? null : CtlReceivedAt ?? Timestamp,
            Ltc is null ? null : LtcReceivedAt ?? Timestamp,
            Vitc is null ? null : VitcReceivedAt ?? Timestamp,
            LtcUserBits is null ? null : LtcUserBitsReceivedAt ?? Timestamp,
            VitcUserBits is null ? null : VitcUserBitsReceivedAt ?? Timestamp,
        }.Max();

    /// <summary>
    /// Returns the primary observation when it is fresh, then tries fresh LTC, VITC,
    /// and CTL fallbacks. Returns null for stale, lost, or unavailable data.
    /// </summary>
    public TimeObservation<Timecode>? GetPrimaryObservation(
        DateTimeOffset now,
        TimeSpan? staleAfter = null,
        TimeSpan? lostAfter = null)
        => GetPrimaryObservationCore(now, null, staleAfter, lostAfter);

    /// <summary>Monotonic-clock variant used by live UI and recording metadata.</summary>
    public TimeObservation<Timecode>? GetPrimaryObservation(
        DateTimeOffset now,
        long nowTimestamp100ns,
        TimeSpan? staleAfter = null,
        TimeSpan? lostAfter = null)
        => GetPrimaryObservationCore(now, nowTimestamp100ns, staleAfter, lostAfter);

    private TimeObservation<Timecode>? GetPrimaryObservationCore(
        DateTimeOffset now,
        long? nowTimestamp100ns,
        TimeSpan? staleAfter,
        TimeSpan? lostAfter)
    {
        var stale = staleAfter ?? DefaultStaleAfter;
        var lost = lostAfter ?? DefaultLostAfter;
        var primary = PrimarySource switch
        {
            TimecodeSource.Vitc or TimecodeSource.HoldVitc => VitcObservation,
            TimecodeSource.Ctl or TimecodeSource.Ctl2 => CtlObservation,
            TimecodeSource.Ltc or TimecodeSource.CorrectedLtc or TimecodeSource.HoldLtc => LtcObservation,
            _ => null,
        };

        if (IsFresh(primary, now, nowTimestamp100ns, stale, lost)) return primary;
        if (IsFresh(LtcObservation, now, nowTimestamp100ns, stale, lost)) return LtcObservation;
        if (IsFresh(VitcObservation, now, nowTimestamp100ns, stale, lost)) return VitcObservation;
        if (IsFresh(CtlObservation, now, nowTimestamp100ns, stale, lost)) return CtlObservation;
        return null;
    }

    public static ObservationFreshness ClassifyFreshness(
        DateTimeOffset? receivedAt,
        DateTimeOffset now,
        TimeSpan? staleAfter = null,
        TimeSpan? lostAfter = null)
    {
        if (receivedAt is null) return ObservationFreshness.Unavailable;

        var stale = staleAfter ?? DefaultStaleAfter;
        var lost = lostAfter ?? DefaultLostAfter;
        if (stale < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        if (lost <= stale) throw new ArgumentOutOfRangeException(nameof(lostAfter));

        var age = now - receivedAt.Value;
        if (age <= stale) return ObservationFreshness.Fresh;
        if (age <= lost) return ObservationFreshness.Stale;
        return ObservationFreshness.Lost;
    }

    private static bool IsFresh<T>(
        TimeObservation<T>? observation,
        DateTimeOffset now,
        long? nowTimestamp100ns,
        TimeSpan staleAfter,
        TimeSpan lostAfter)
        => observation is { } value
           && (nowTimestamp100ns is { } monotonicNow
                ? value.FreshnessAt(monotonicNow, now, staleAfter, lostAfter)
                : value.FreshnessAt(now, staleAfter, lostAfter)) == ObservationFreshness.Fresh;
}
