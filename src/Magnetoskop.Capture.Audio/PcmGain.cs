namespace Magnetoskop.Capture.Audio;

/// <summary>
/// In-place linear gain for interleaved little-endian PCM used by the audio monitor.
/// Clamps after scaling so boost above 1.0 does not wrap.
/// </summary>
public static class PcmGain
{
    /// <summary>
    /// Applies <paramref name="gain"/> to <paramref name="data"/> (first <paramref name="length"/> bytes).
    /// Gain of ~1 leaves samples unchanged; 0 silences; values above 1 boost with clamp.
    /// </summary>
    public static void Apply(byte[] data, int length, int bitsPerSample, float gain)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (length < 0 || length > data.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (bitsPerSample is not (16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

        if (Math.Abs(gain - 1f) < 0.0001f)
            return;

        if (gain <= 0f)
        {
            Array.Clear(data, 0, length);
            return;
        }

        switch (bitsPerSample)
        {
            case 16:
                Apply16(data.AsSpan(0, length), gain);
                break;
            case 24:
                Apply24(data.AsSpan(0, length), gain);
                break;
            case 32:
                Apply32(data.AsSpan(0, length), gain);
                break;
        }
    }

    private static void Apply16(Span<byte> data, float gain)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var sample = (short)(data[i] | (data[i + 1] << 8));
            var scaled = (int)Math.Round(sample * (double)gain);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            data[i] = (byte)(scaled & 0xFF);
            data[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    private static void Apply24(Span<byte> data, float gain)
    {
        for (var i = 0; i + 2 < data.Length; i += 3)
        {
            var sample = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000); // sign-extend
            var scaled = (int)Math.Round(sample * (double)gain);
            scaled = Math.Clamp(scaled, -8_388_608, 8_388_607);
            data[i] = (byte)(scaled & 0xFF);
            data[i + 1] = (byte)((scaled >> 8) & 0xFF);
            data[i + 2] = (byte)((scaled >> 16) & 0xFF);
        }
    }

    private static void Apply32(Span<byte> data, float gain)
    {
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            var sample = data[i]
                | (data[i + 1] << 8)
                | (data[i + 2] << 16)
                | (data[i + 3] << 24);
            var scaled = (long)Math.Round(sample * (double)gain);
            scaled = Math.Clamp(scaled, int.MinValue, int.MaxValue);
            var s = (int)scaled;
            data[i] = (byte)(s & 0xFF);
            data[i + 1] = (byte)((s >> 8) & 0xFF);
            data[i + 2] = (byte)((s >> 16) & 0xFF);
            data[i + 3] = (byte)((s >> 24) & 0xFF);
        }
    }
}
