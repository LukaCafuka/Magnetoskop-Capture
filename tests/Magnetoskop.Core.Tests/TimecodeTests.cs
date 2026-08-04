using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Tests;

public class TimecodeTests
{
    [Fact]
    public void ToString_FormatsNonDropFrame()
    {
        var tc = new Timecode(1, 2, 3, 4);
        Assert.Equal("01:02:03:04", tc.ToString());
    }

    [Fact]
    public void ToString_UsesSemicolonForDropFrame()
    {
        var tc = new Timecode(1, 2, 3, 4, DropFrame: true);
        Assert.Equal("01:02:03;04", tc.ToString());
    }

    [Fact]
    public void ToString_PrefixesMinusWhenNegative()
    {
        var tc = new Timecode(0, 0, 0, 1, IsNegative: true);
        Assert.Equal("-00:00:00:01", tc.ToString());
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(24, 0, 0, 0, 24)]       // 24 frames @ 25fps = 00:00:00:24
    [InlineData(25, 0, 0, 1, 0)]        // 25 frames @ 25fps = 00:00:01:00
    [InlineData(90_000, 1, 0, 0, 0)]    // 1h @ 25fps
    public void FromFrameCount_RoundTrips(long frames, int h, int m, int s, int f)
    {
        var tc = Timecode.FromFrameCount(frames, 25);
        Assert.Equal(new Timecode(h, m, s, f), tc);
        Assert.Equal(frames, tc.ToFrameCount(25));
    }

    [Fact]
    public void FromFrameCount_PreservesNegative()
    {
        var tc = Timecode.FromFrameCount(-26, 25); // -00:00:01:01
        Assert.True(tc.IsNegative);
        Assert.Equal(0, tc.Hours);
        Assert.Equal(0, tc.Minutes);
        Assert.Equal(1, tc.Seconds);
        Assert.Equal(1, tc.Frames);
        Assert.Equal(-26, tc.ToFrameCount(25));
        Assert.Equal("-00:00:01:01", tc.ToString());
    }

    [Fact]
    public void FromFrameCount_WrapsAt24Hours()
    {
        var frames = 25L * 60 * 60 * 25; // 25 hours at 25 fps
        var tc = Timecode.FromFrameCount(frames, 25);
        Assert.Equal(1, tc.Hours);
    }

    [Theory]
    [InlineData("01:02:03:04", 1, 2, 3, 4, false)]
    [InlineData("1:2:3:4", 1, 2, 3, 4, false)]
    [InlineData("01:02:03;04", 1, 2, 3, 4, true)]
    [InlineData("1:00:00", 0, 1, 0, 0, false)]
    [InlineData("01:02:03", 0, 1, 2, 3, false)]
    public void TryParse_AcceptsHhMmSsFf(string text, int h, int m, int s, int f, bool drop)
    {
        Assert.True(Timecode.TryParse(text, out var tc));
        Assert.Equal(h, tc.Hours);
        Assert.Equal(m, tc.Minutes);
        Assert.Equal(s, tc.Seconds);
        Assert.Equal(f, tc.Frames);
        Assert.Equal(drop, tc.DropFrame);
        Assert.False(tc.IsNegative);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01:02")]
    [InlineData("-01:00:00:00")]
    [InlineData("99:00:00:00")]
    [InlineData("ab:cd:ef:gh")]
    public void TryParse_RejectsInvalid(string? text)
    {
        Assert.False(Timecode.TryParse(text, out _));
    }

    [Fact]
    public void InterpretAsSignedCtl_ConvertsWrappedNearMidnight()
    {
        // -1 frame @ 25 fps wraps to 23:59:59:24 on a 24h CTL counter.
        var wrapped = new Timecode(23, 59, 59, 24);
        var signed = Timecode.InterpretAsSignedCtl(wrapped, 25);
        Assert.True(signed.IsNegative);
        Assert.Equal("-00:00:00:01", signed.ToString());
        Assert.Equal(-1, signed.ToFrameCount(25));
    }

    [Fact]
    public void InterpretAsSignedCtl_LeavesMorningValuesAlone()
    {
        var morning = new Timecode(1, 2, 3, 4);
        Assert.Equal(morning, Timecode.InterpretAsSignedCtl(morning, 25));
    }

    [Fact]
    public void FormatCtlDisplay_SignedByDefault()
    {
        var wrapped = new Timecode(23, 59, 59, 24);
        Assert.Equal("-00:00:00:01", Timecode.FormatCtlDisplay(wrapped, use24HourWrap: false));
    }

    [Fact]
    public void FormatCtlDisplay_WrapModeKeeps24HourWrap()
    {
        var wrapped = new Timecode(23, 59, 59, 24);
        Assert.Equal("23:59:59:24", Timecode.FormatCtlDisplay(wrapped, use24HourWrap: true));
    }

    [Fact]
    public void FormatCtlDisplay_WrapModeExpandsSignedToWrap()
    {
        var signed = new Timecode(0, 0, 0, 1, IsNegative: true);
        Assert.Equal("23:59:59:24", Timecode.FormatCtlDisplay(signed, use24HourWrap: true));
    }

    [Fact]
    public void FormatCtlDisplay_NullIsPlaceholder()
    {
        Assert.Equal("--:--:--:--", Timecode.FormatCtlDisplay(null, use24HourWrap: false));
    }
}