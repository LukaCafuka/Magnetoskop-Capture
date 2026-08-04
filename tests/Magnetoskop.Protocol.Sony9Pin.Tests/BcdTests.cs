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
    public void EncodeTimecode_SetsNegativeSignOnHoursByte()
    {
        var data = Bcd.EncodeTimecode(new Timecode(0, 0, 0, 1, IsNegative: true));
        Assert.Equal(0x40, data[3]); // sign bit, hours 00
        Assert.Equal(0x01, data[0]);
    }

    [Fact]
    public void DecodeTimecode_ReadsNegativeSignFromHoursByte()
    {
        // hours 0x40 = sign + 00 → -00:00:00:05
        var tc = Bcd.DecodeTimecode(new byte[] { 0x05, 0x00, 0x00, 0x40 });
        Assert.True(tc.IsNegative);
        Assert.Equal(0, tc.Hours);
        Assert.Equal(5, tc.Frames);
        Assert.Equal("-00:00:00:05", tc.ToString());
    }

    [Fact]
    public void DecodeTimecode_RoundTripsNegative()
    {
        var original = new Timecode(0, 1, 2, 3, IsNegative: true);
        var decoded = Bcd.DecodeTimecode(Bcd.EncodeTimecode(original));
        Assert.Equal(original, decoded);
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