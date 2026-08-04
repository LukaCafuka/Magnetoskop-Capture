using Magnetoskop.Core.Models;

namespace Magnetoskop.Capture.Video;

/// <summary>
/// Realtime bob 2× deinterlace for live preview: each woven interlaced BGR24 frame
/// becomes two progressive frames (one per field, line-doubled). Does not match
/// FFmpeg yadif quality; intended for easier watching only.
/// </summary>
public sealed class PreviewBobDeinterlacer
{
    private byte[]? _first;
    private byte[]? _second;

    /// <summary>
    /// When the format is interlaced BGR24 with height ≥ 2, writes two progressive
    /// frames into reused buffers (temporal order: TFF → top then bottom; BFF → reverse)
    /// and returns <c>true</c>. Otherwise returns <c>false</c> (caller should show the woven frame).
    /// </summary>
    /// <remarks>
    /// Buffer contents are valid until the next call on this instance. Callers must finish
    /// reading (e.g. WritePixels) before the next <see cref="TryDeinterlace"/>.
    /// </remarks>
    public bool TryDeinterlace(byte[] wovenBgr24, VideoFormat format, out byte[] first, out byte[] second)
    {
        first = Array.Empty<byte>();
        second = Array.Empty<byte>();

        if (!format.Interlaced
            || format.Height < 2
            || format.PixelFormat != VideoPixelFormat.Bgr24)
        {
            return false;
        }

        var stride = format.Width * 3;
        var expected = stride * format.Height;
        if (wovenBgr24.Length < expected || format.Width <= 0)
        {
            return false;
        }

        EnsureBuffers(expected);
        BobField(wovenBgr24, _first!, stride, format.Height, evenSourceLines: true);
        BobField(wovenBgr24, _second!, stride, format.Height, evenSourceLines: false);

        if (format.TopFieldFirst)
        {
            first = _first!;
            second = _second!;
        }
        else
        {
            first = _second!;
            second = _first!;
        }

        return true;
    }

    private void EnsureBuffers(int size)
    {
        if (_first is null || _first.Length != size)
        {
            _first = new byte[size];
        }

        if (_second is null || _second.Length != size)
        {
            _second = new byte[size];
        }
    }

    /// <summary>
    /// Line-double one field into a full-height progressive frame.
    /// Even source lines = top field; odd = bottom field (woven storage).
    /// </summary>
    private static void BobField(byte[] src, byte[] dst, int stride, int height, bool evenSourceLines)
    {
        for (var y = 0; y < height; y++)
        {
            var srcY = evenSourceLines ? (y / 2) * 2 : (y / 2) * 2 + 1;
            if (srcY >= height)
            {
                srcY = height - 1;
            }

            Buffer.BlockCopy(src, srcY * stride, dst, y * stride, stride);
        }
    }
}
