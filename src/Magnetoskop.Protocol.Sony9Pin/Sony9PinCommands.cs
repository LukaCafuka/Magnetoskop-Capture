namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// Factory for the Sony 9-pin command blocks used by this application.
/// Codes taken from the DVR-2000/2100 command table in context_source/sony_9pin.
/// </summary>
public static class Sony9PinCommands
{
    // ---- System control (group 0) ----
    public static CommandBlock LocalDisable() => new(0x00, 0x0C);
    public static CommandBlock DeviceTypeRequest() => new(0x00, 0x11);
    public static CommandBlock LocalEnable() => new(0x00, 0x1D);

    // ---- Transport control (group 2) ----
    public static CommandBlock Stop() => new(0x20, 0x00);
    public static CommandBlock Play() => new(0x20, 0x01);
    public static CommandBlock Record() => new(0x20, 0x02);
    public static CommandBlock StandbyOff() => new(0x20, 0x04);
    public static CommandBlock StandbyOn() => new(0x20, 0x05);
    public static CommandBlock Eject() => new(0x20, 0x0F);
    public static CommandBlock FastForward() => new(0x20, 0x10);
    /// <summary>20 14 FRAME STEP forward — one frame, then still.</summary>
    public static CommandBlock FrameStepForward() => new(0x20, 0x14);
    public static CommandBlock Rewind() => new(0x20, 0x20);
    /// <summary>20 24 FRAME STEP reverse — one frame back, then still.</summary>
    public static CommandBlock FrameStepReverse() => new(0x20, 0x24);
    public static CommandBlock Preroll() => new(0x20, 0x30);

    /// <summary>
    /// Still/pause without Stop: Shuttle Fwd with speed 0 (<c>21 13 00</c>).
    /// Editors use this to pause while remaining threaded.
    /// </summary>
    public static CommandBlock Pause()
        => ShuttleForward(Core.Models.VariableSpeedEncoding.Still);

    /// <summary>2X 11 Jog Fwd with one speed byte (see speed formula in the reference).</summary>
    public static CommandBlock JogForward(byte speed) => new(0x21, 0x11, speed);
    public static CommandBlock JogReverse(byte speed) => new(0x21, 0x21, speed);
    public static CommandBlock VarForward(byte speed) => new(0x21, 0x12, speed);
    public static CommandBlock VarReverse(byte speed) => new(0x21, 0x22, speed);
    public static CommandBlock ShuttleForward(byte speed) => new(0x21, 0x13, speed);
    public static CommandBlock ShuttleReverse(byte speed) => new(0x21, 0x23, speed);

    /// <summary>24 31 Cue Up With Data. Time is BCD frames/seconds/minutes/hours.</summary>
    public static CommandBlock CueUpWithData(Core.Models.Timecode timecode, int framesPerSecond = 25)
        => new(0x24, 0x31, Bcd.EncodeTimecode(timecode, framesPerSecond));

    /// <summary>
    /// 40 08 Timer-1 Reset — zeroes the CTL (Timer-1) counter at the current tape position
    /// without seeking.
    /// </summary>
    public static CommandBlock Timer1Reset() => new(0x40, 0x08);

    /// <summary>
    /// 41 36 Timer Mode Select. DATA-1 = 00 TIME CODE, 01 TIMER-1, 02 TIMER-2.
    /// Cue Up With Data follows this mode.
    /// </summary>
    public static CommandBlock TimerModeSelect(Core.Models.CueUpTimerMode mode)
        => new(0x41, 0x36, (byte)mode);

    /// <summary>41 36 with TIME CODE (LTC go-to).</summary>
    public static CommandBlock TimerModeSelectTimeCode()
        => TimerModeSelect(Core.Models.CueUpTimerMode.TimeCode);

    // ---- Sense requests (group 6) ----

    /// <summary>
    /// 61 20 Status Sense. DATA-1 high nibble = starting status byte, low nibble = count.
    /// Default requests bytes 0..9 (the full documented set).
    /// </summary>
    public static CommandBlock StatusSense(int startByte = 0, int count = 10)
    {
        if (startByte is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(startByte));
        if (count is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(count));
        return new CommandBlock(0x61, 0x20, (byte)(startByte << 4 | count));
    }

    /// <summary>61 0C Current Time Sense with the given source bitmask.</summary>
    public static CommandBlock CurrentTimeSense(TimeSenseRequest request)
        => new(0x61, 0x0C, (byte)request);
}

/// <summary>
/// DATA-1 bitmask for 61 0C Current Time Sense (per the reference):
/// bit0 LTC time, bit1 VITC time, bit2 Timer-1, bit3 Timer-2, bit4 LTC UB, bit5 VITC UB.
/// The combination LTC|VITC (0x03) asks the deck for the best available timecode.
/// </summary>
[Flags]
public enum TimeSenseRequest : byte
{
    LtcTime = 0x01,
    VitcTime = 0x02,
    Timer1 = 0x04,
    Timer2 = 0x08,
    LtcUserBits = 0x10,
    VitcUserBits = 0x20,
    /// <summary>Deck chooses LTC, VITC, or corrected LTC depending on tape speed/signal.</summary>
    BestTimecode = LtcTime | VitcTime,
}