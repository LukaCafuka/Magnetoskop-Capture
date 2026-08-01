using Magnetoskop.Core.Models;

namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>NAK error bits (11 12 response DATA-1), per the reference.</summary>
[Flags]
public enum NakError : byte
{
    None = 0,
    UndefinedCommand = 0x01,
    ChecksumError = 0x04,
    ParityError = 0x10,
    OverrunError = 0x20,
    FramingError = 0x40,
    TimeOut = 0x80,
}

/// <summary>Classified slave→master responses.</summary>
public abstract record Sony9PinResponse
{
    public required CommandBlock Raw { get; init; }

    /// <summary>10 01 ACK.</summary>
    public sealed record Ack : Sony9PinResponse;

    /// <summary>11 12 NAK + error bits.</summary>
    public sealed record Nak(NakError Error) : Sony9PinResponse
    {
        /// <summary>Per spec: after an undefined-command NAK the master may send the next
        /// command immediately; after any other error it must wait ≥ 10 ms.</summary>
        public bool IsUndefinedCommand => Error.HasFlag(NakError.UndefinedCommand);
    }

    /// <summary>12 11 Device Type + 2 data bytes.</summary>
    public sealed record DeviceType(byte Byte1, byte Byte2) : Sony9PinResponse
    {
        public string Code => $"{Byte1:X2} {Byte2:X2}";
    }

    /// <summary>7X 20 Status Data (up to 10 documented bytes).</summary>
    public sealed record StatusData(IReadOnlyList<byte> Bytes) : Sony9PinResponse;

    /// <summary>74 0X / 74 1X time data responses.</summary>
    public sealed record TimeData(TimeDataKind Kind, Timecode Timecode) : Sony9PinResponse;

    /// <summary>74 05/07/15/17 user-bit responses.</summary>
    public sealed record UserBitsData(TimeDataKind Kind, UserBits UserBits) : Sony9PinResponse;

    /// <summary>Any response we do not classify; kept raw for logging/diagnostics.</summary>
    public sealed record Unknown : Sony9PinResponse;
}

/// <summary>Which register a 74 xx time/user-bit response refers to.</summary>
public enum TimeDataKind
{
    Timer1,          // 74 00 (CTL)
    Timer2,          // 74 01 (CTL)
    LtcTime,         // 74 04
    LtcUserBits,     // 74 05
    VitcTime,        // 74 06
    VitcUserBits,    // 74 07
    GenTime,         // 74 08
    GenUserBits,     // 79 09
    CorrectedLtcTime,// 74 14
    HoldLtcUserBits, // 74 15
    HoldVitcTime,    // 74 16
    HoldVitcUserBits,// 74 17
}

/// <summary>Classifies parsed command blocks into typed responses.</summary>
public static class Sony9PinResponseParser
{
    public static Sony9PinResponse Classify(CommandBlock block)
    {
        return (block.Cmd1 & 0xF0, block.Cmd2) switch
        {
            (0x10, 0x01) => new Sony9PinResponse.Ack { Raw = block },
            (0x10, 0x12) => new Sony9PinResponse.Nak(
                block.Data.Count > 0 ? (NakError)block.Data[0] : NakError.None)
                { Raw = block },
            (0x10, 0x11) or (0x20, 0x11) when block.CommandGroup == 1 => DeviceType(block),
            (0x70, 0x20) => new Sony9PinResponse.StatusData(block.Data) { Raw = block },
            (0x70, _) when block.CommandGroup == 7 => ClassifyGroup7(block),
            _ when block.CommandGroup == 1 && block.Cmd2 == 0x11 => DeviceType(block),
            _ => new Sony9PinResponse.Unknown { Raw = block },
        };
    }

    private static Sony9PinResponse DeviceType(CommandBlock block)
        => block.Data.Count >= 2
            ? new Sony9PinResponse.DeviceType(block.Data[0], block.Data[1]) { Raw = block }
            : new Sony9PinResponse.Unknown { Raw = block };

    private static Sony9PinResponse ClassifyGroup7(CommandBlock block)
    {
        TimeDataKind? kind = block.Cmd2 switch
        {
            0x00 => TimeDataKind.Timer1,
            0x01 => TimeDataKind.Timer2,
            0x04 => TimeDataKind.LtcTime,
            0x05 => TimeDataKind.LtcUserBits,
            0x06 => TimeDataKind.VitcTime,
            0x07 => TimeDataKind.VitcUserBits,
            0x08 => TimeDataKind.GenTime,
            0x09 => TimeDataKind.GenUserBits,
            0x14 => TimeDataKind.CorrectedLtcTime,
            0x15 => TimeDataKind.HoldLtcUserBits,
            0x16 => TimeDataKind.HoldVitcTime,
            0x17 => TimeDataKind.HoldVitcUserBits,
            _ => null,
        };

        if (kind is null || block.Data.Count < 4)
        {
            return new Sony9PinResponse.Unknown { Raw = block };
        }

        var data = block.Data.ToArray();
        return kind switch
        {
            TimeDataKind.LtcUserBits or TimeDataKind.VitcUserBits
                or TimeDataKind.GenUserBits or TimeDataKind.HoldLtcUserBits
                or TimeDataKind.HoldVitcUserBits
                => new Sony9PinResponse.UserBitsData(kind.Value, Bcd.DecodeUserBits(data)) { Raw = block },
            _ => new Sony9PinResponse.TimeData(kind.Value, Bcd.DecodeTimecode(data)) { Raw = block },
        };
    }
}

/// <summary>
/// Decodes 7X 20 Status Data bytes into a <see cref="VtrStatus"/>.
/// Bit layout per the reference (Data 0..9). Only bits used by the application are mapped;
/// the raw bytes stay available for diagnostics.
/// </summary>
public static class StatusBitsParser
{
    public static VtrStatus Parse(IReadOnlyList<byte> data, bool isConnected = true)
    {
        byte D(int i) => i < data.Count ? data[i] : (byte)0;

        var d0 = D(0);
        var d1 = D(1);
        var d2 = D(2);
        var d8 = D(8);

        var transport = DecodeTransport(d1, d2);

        return new VtrStatus
        {
            IsConnected = isConnected,
            Transport = transport,
            IsLocal = (d0 & 0x01) != 0,
            TapeOut = (d0 & 0x20) != 0,
            ServoRefMissing = (d0 & 0x10) != 0,
            ServoLock = (d2 & 0x80) != 0,
            TapeReverse = (d2 & 0x04) != 0,
            RecordInhibited = (d8 & 0x01) != 0,
            SystemAlarm = (d8 & 0x02) != 0,
            ServoAlarm = (d8 & 0x04) != 0,
            EndOfTape = (d8 & 0x10) != 0,
            NearEndOfTape = (d8 & 0x20) != 0,
        };
    }

    private static TransportState DecodeTransport(byte d1, byte d2)
    {
        // Data-1: bit7 Standby, bit5 Stop, bit4 Eject, bit3 Rewind, bit2 FastFwd, bit1 Record, bit0 Play
        if ((d1 & 0x10) != 0) return TransportState.Ejecting;
        if ((d1 & 0x02) != 0) return TransportState.Recording;
        if ((d1 & 0x01) != 0) return TransportState.Playing;
        if ((d1 & 0x04) != 0) return TransportState.FastForwarding;
        if ((d1 & 0x08) != 0) return TransportState.Rewinding;

        // Data-2: bit5 Shuttle, bit4 Jog, bit3 Var, bit1 Still
        if ((d2 & 0x20) != 0) return TransportState.Shuttle;
        if ((d2 & 0x10) != 0) return TransportState.Jog;
        if ((d2 & 0x08) != 0) return TransportState.Var;
        if ((d2 & 0x02) != 0) return TransportState.Still;

        if ((d1 & 0x20) != 0) return TransportState.Stopped;
        if ((d1 & 0x80) != 0) return TransportState.Standby;
        return TransportState.Unknown;
    }
}