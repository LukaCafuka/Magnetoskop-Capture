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
    public void FromFrameCount_ClampsNegativeToZero()
    {
        Assert.Equal(Timecode.Zero, Timecode.FromFrameCount(-10, 25));
    }

    [Fact]
    public void FromFrameCount_WrapsAt24Hours()
    {
        var frames = 25L * 60 * 60 * 25; // 25 hours at 25 fps
        var tc = Timecode.FromFrameCount(frames, 25);
        Assert.Equal(1, tc.Hours);
    }
}