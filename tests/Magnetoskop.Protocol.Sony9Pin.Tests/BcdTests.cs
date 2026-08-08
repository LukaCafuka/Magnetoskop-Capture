using Magnetoskop.Core.Models;

namespace Magnetoskop.Protocol.Sony9Pin.Tests;

public class BcdTests
{
    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(9, 0x09)]
    [InlineData(10, 0x10)]
    [InlineData(59, 0x59)]
    [InlineData(99, 0x99)]
    public void EncodeDigit_ProducesBcd(int value, byte expected)
    {
        Assert.Equal(expected, Bcd.EncodeDigit(value));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x25, 25)]
    [InlineData(0x59, 59)]
    public void DecodeDigit_ParsesBcd(byte bcd, int expected)
    {
        Assert.Equal(expected, Bcd.DecodeDigit(bcd));
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0xA0)]
    [InlineData(0xFF)]
    public void DecodeDigit_ThrowsOnInvalidNibbles(byte bcd)
    {
        Assert.Throws<FormatException>(() => Bcd.DecodeDigit(bcd));
    }

    [Fact]
    public void EncodeTimecode_OrdersFramesSecondsMinutesHours()
    {
        // 12:34:56:12 -> frames 0x12, seconds 0x56, minutes 0x34, hours 0x12
        var data = Bcd.EncodeTimecode(new Timecode(12, 34, 56, 12));
        Assert.Equal(new byte[] { 0x12, 0x56, 0x34, 0x12 }, data);
    }

    [Fact]
    public void EncodeTimecode_SetsDropFrameAndColorFrameFlags()
    {
        var data = Bcd.EncodeTimecode(new Timecode(0, 0, 0, 5, DropFrame: true, ColorFrame: true));
        Assert.Equal(0x80 | 0x40 | 0x05, data[0]);
    }

    [Fact]
    public void DecodeTimecode_RoundTrips()
    {
        var original = new Timecode(23, 59, 59, 24, DropFrame: true, ColorFrame: false);
        var decoded = Bcd.DecodeTimecode(Bcd.EncodeTimecode(original));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeTimecode_MasksFlagBitsFromFrames()
    {
        // frames byte = CF|DF|0x21 -> frames 21, both flags on
        var tc = Bcd.DecodeTimecode(new byte[] { 0xE1, 0x00, 0x00, 0x01 });
        Assert.Equal(21, tc.Frames);
        Assert.True(tc.DropFrame);
        Assert.True(tc.ColorFrame);
        Assert.Equal(1, tc.Hours);
    }

    [Fact]
    public void EncodeTimecode_NegativeCtl_Uses24HourWrapNotSignBit()
    {
        // -00:00:00:01 @ 25 fps → 23:59:59:24 on the wire (hours must not be 0x40).
        var data = Bcd.EncodeTimecode(new Timecode(0, 0, 0, 1, IsNegative: true), framesPerSecond: 25);
        Assert.Equal(new byte[] { 0x24, 0x59, 0x59, 0x23 }, data);
    }

    [Fact]
    public void EncodeTimecode_NegativeCtl_FourteenSeconds_Wraps()
    {
        // Matches the failed cue from the debug log: -00:00:14:23 must not send hours 0x40.
        var data = Bcd.EncodeTimecode(new Timecode(0, 0, 14, 23, IsNegative: true), framesPerSecond: 25);
        Assert.NotEqual(0x40, data[3] & 0xC0); // no hours sign / high bits
        Assert.Equal(0x23, data[3]); // hours in the 23:xx wrap range
        var decoded = Bcd.DecodeTimecode(data);
        var signed = Timecode.InterpretAsSignedCtl(decoded, 25);
        Assert.True(signed.IsNegative);
        Assert.Equal(0, signed.Hours);
        Assert.Equal(0, signed.Minutes);
        Assert.Equal(14, signed.Seconds);
        Assert.Equal(23, signed.Frames);
    }

    [Fact]
    public void DecodeTimecode_MasksHoursHighBitsWithoutTreatingAsSign()
    {
        // Legacy hours 0x40 is masked to hours 00; sign is not inferred (wrap is used instead).
        var tc = Bcd.DecodeTimecode(new byte[] { 0x05, 0x00, 0x00, 0x40 });
        Assert.False(tc.IsNegative);
        Assert.Equal(0, tc.Hours);
        Assert.Equal(5, tc.Frames);
    }

    [Fact]
    public void DecodeTimecode_RequiresFourBytes()
    {
        Assert.Throws<ArgumentException>(() => Bcd.DecodeTimecode(new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void DecodeUserBits_PassesRawBytesThrough()
    {
        var ub = Bcd.DecodeUserBits(new byte[] { 0xAB, 0xCD, 0xEF, 0x12 });
        Assert.Equal(new UserBits(0xAB, 0xCD, 0xEF, 0x12), ub);
    }
}