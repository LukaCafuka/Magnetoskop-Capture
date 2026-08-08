using Magnetoskop.Core.Models;

namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// BCD encoding used by all Sony 9-pin time data:
/// DATA-1..DATA-4 = frames, seconds, minutes, hours; tens digit in the high nibble.
/// For LTC-style values, DATA-1 bit 7 = Color Frame flag, bit 6 = Drop Frame flag.
/// </summary>
public static class Bcd
{
    public static byte EncodeDigit(int value)
    {
        if (value is < 0 or > 99) throw new ArgumentOutOfRangeException(nameof(value));
        return (byte)(value / 10 << 4 | value % 10);
    }

    public static int DecodeDigit(byte bcd)
    {
        var tens = bcd >> 4 & 0x0F;
        var ones = bcd & 0x0F;
        if (tens > 9 || ones > 9)
        {
            throw new FormatException($"Invalid BCD byte 0x{bcd:X2}.");
        }
        return tens * 10 + ones;
    }

    /// <summary>
    /// Encodes a timecode as 4 BCD bytes (frames, seconds, minutes, hours) with CF/DF flags.
    /// Signed CTL values are converted to 24-hour wrap (deck wire format) — decks do not
    /// honor a hours sign bit on Cue Up and would seek to the positive magnitude instead.
    /// </summary>
    public static byte[] EncodeTimecode(Timecode tc, int framesPerSecond = 25)
    {
        if (tc.IsNegative)
        {
            tc = Timecode.To24HourCtlWrap(tc, framesPerSecond);
        }

        var frames = EncodeDigit(tc.Frames);
        if (tc.ColorFrame) frames |= 0x80;
        if (tc.DropFrame) frames |= 0x40;
        return new[]
        {
            frames,
            EncodeDigit(tc.Seconds),
            EncodeDigit(tc.Minutes),
            EncodeDigit(tc.Hours),
        };
    }

    /// <summary>Decodes 4 BCD bytes (frames, seconds, minutes, hours) into a timecode.
    /// Bits 7/6 of the frames byte carry the CF/DF flags and are masked out.
    /// Bit 6/7 of the hours byte are masked out of the hours BCD (legacy; decks use 24h wrap
    /// for below-zero CTL — apply <see cref="Timecode.InterpretAsSignedCtl"/> for display).</summary>
    public static Timecode DecodeTimecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new ArgumentException("Timecode data requires 4 bytes.", nameof(data));
        }

        var colorFrame = (data[0] & 0x80) != 0;
        var dropFrame = (data[0] & 0x40) != 0;
        // Hours bit 6/7 are not used as a CTL sign on real decks (wrap is used instead),
        // but still mask them out of the BCD hours nibble.
        return new Timecode(
            Hours: DecodeDigit((byte)(data[3] & 0x3F)),
            Minutes: DecodeDigit(data[2]),
            Seconds: DecodeDigit(data[1]),
            Frames: DecodeDigit((byte)(data[0] & 0x3F)),
            DropFrame: dropFrame,
            ColorFrame: colorFrame);
    }

    /// <summary>User bits are transported raw as 4 bytes (2 binary groups per byte); no BCD math.</summary>
    public static UserBits DecodeUserBits(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new ArgumentException("User bits data requires 4 bytes.", nameof(data));
        }
        return new UserBits(data[0], data[1], data[2], data[3]);
    }
}
