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
    public Timecode? Ctl { get; init; }
    public Timecode? Ltc { get; init; }
    public Timecode? Vitc { get; init; }
    public UserBits? LtcUserBits { get; init; }
    public UserBits? VitcUserBits { get; init; }
    /// <summary>Which source produced the primary display timecode.</summary>
    public TimecodeSource? PrimarySource { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}