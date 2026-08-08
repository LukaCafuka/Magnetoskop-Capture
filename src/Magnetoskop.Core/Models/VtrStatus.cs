namespace Magnetoskop.Core.Models;

/// <summary>Transport modes as reported by / commanded to a VTR.</summary>
public enum TransportState
{
    Unknown,
    Stopped,
    Playing,
    Recording,
    FastForwarding,
    Rewinding,
    Ejecting,
    Still,
    Jog,
    Shuttle,
    Var,
    Standby,
}

/// <summary>Transport commands. The initial set is required; the rest are reserved for later phases.</summary>
public enum TransportCommand
{
    Play,
    Stop,
    FastForward,
    Rewind,
    Eject,
    /// <summary>Still/pause without Stop (Sony 9-pin Shuttle 0).</summary>
    Pause,
    /// <summary>Sony FRAME STEP forward (<c>20 14</c>) — one frame, then still.</summary>
    FrameStepForward,
    /// <summary>Sony FRAME STEP reverse (<c>20 24</c>) — one frame back, then still.</summary>
    FrameStepReverse,
    /// <summary>Sony Timer-1 Reset (<c>40 08</c>) — zeroes the CTL counter at the current tape position.</summary>
    Timer1Reset,
    // Reserved for later phases:
    Record,
    StandbyOn,
    StandbyOff,
    JogForward,
    JogReverse,
    ShuttleForward,
    ShuttleReverse,
    VarForward,
    VarReverse,
    CueUp,
    Preroll,
}

/// <summary>Variable-speed transport modes (Sony 9-pin Jog / Shuttle).</summary>
public enum VariableSpeedMode
{
    Jog,
    Shuttle,
}

/// <summary>A snapshot of the recorder status.</summary>
public sealed record VtrStatus
{
    public TransportState Transport { get; init; } = TransportState.Unknown;
    public bool IsConnected { get; init; }
    /// <summary>True when the deck's local/remote switch inhibits remote control.</summary>
    public bool IsLocal { get; init; }
    public bool TapeOut { get; init; }
    /// <summary>
    /// Status Data-1 bit 7: tape threaded / scanner locked while stopped (Standby On).
    /// Independent of <see cref="Transport"/> (a stopped deck may still report Standby).
    /// </summary>
    public bool Standby { get; init; }
    public bool ServoLock { get; init; }
    public bool ServoRefMissing { get; init; }
    public bool RecordInhibited { get; init; }
    public bool NearEndOfTape { get; init; }
    public bool EndOfTape { get; init; }
    public bool SystemAlarm { get; init; }
    public bool ServoAlarm { get; init; }
    /// <summary>Tape direction: true = reverse.</summary>
    public bool TapeReverse { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}