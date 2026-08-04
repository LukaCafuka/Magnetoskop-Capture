using Magnetoskop.Capture.Video;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.Tests;

public class PreviewBobDeinterlacerTests
{
    private static VideoFormat PalInterlaced(bool tff = true) => new()
    {
        Width = 4,
        Height = 4,
        FrameRate = 25,
        PixelFormat = VideoPixelFormat.Bgr24,
        Interlaced = true,
        TopFieldFirst = tff,
    };

    /// <summary>
    /// Build a 4×4 BGR woven frame: even lines filled with top marker (10),
    /// odd lines with bottom marker (20). One byte pattern per pixel (B=G=R).
    /// </summary>
    private static byte[] WovenFrame()
    {
        const int w = 4, h = 4, bpp = 3;
        var data = new byte[w * h * bpp];
        for (var y = 0; y < h; y++)
        {
            var marker = (byte)((y % 2 == 0) ? 10 : 20);
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * bpp;
                data[i] = marker;
                data[i + 1] = marker;
                data[i + 2] = marker;
            }
        }

        return data;
    }

    private static byte LineMarker(byte[] frame, int width, int y)
        => frame[y * width * 3];

    [Fact]
    public void TryDeinterlace_Progressive_ReturnsFalse()
    {
        var format = PalInterlaced() with { Interlaced = false };
        var bob = new PreviewBobDeinterlacer();

        Assert.False(bob.TryDeinterlace(WovenFrame(), format, out _, out _));
    }

    [Fact]
    public void TryDeinterlace_Tff_ReturnsTwoFrames_TopThenBottom()
    {
        var bob = new PreviewBobDeinterlacer();
        var ok = bob.TryDeinterlace(WovenFrame(), PalInterlaced(tff: true), out var first, out var second);

        Assert.True(ok);
        Assert.Equal(4 * 4 * 3, first.Length);
        Assert.Equal(4 * 4 * 3, second.Length);

        // First frame is line-doubled top field (even source lines → marker 10).
        Assert.Equal(10, LineMarker(first, 4, 0));
        Assert.Equal(10, LineMarker(first, 4, 1));
        Assert.Equal(10, LineMarker(first, 4, 2));
        Assert.Equal(10, LineMarker(first, 4, 3));

        // Second frame is line-doubled bottom field (odd source lines → marker 20).
        Assert.Equal(20, LineMarker(second, 4, 0));
        Assert.Equal(20, LineMarker(second, 4, 1));
        Assert.Equal(20, LineMarker(second, 4, 2));
        Assert.Equal(20, LineMarker(second, 4, 3));
    }

    [Fact]
    public void TryDeinterlace_Bff_ReturnsBottomThenTop()
    {
        var bob = new PreviewBobDeinterlacer();
        var ok = bob.TryDeinterlace(WovenFrame(), PalInterlaced(tff: false), out var first, out var second);

        Assert.True(ok);
        Assert.Equal(20, LineMarker(first, 4, 0));
        Assert.Equal(10, LineMarker(second, 4, 0));
    }
}
