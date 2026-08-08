using Magnetoskop.Capture.Audio;

namespace Magnetoskop.App.Tests;

public class PcmGainTests
{
    [Fact]
    public void Apply_GainOne_LeavesSamplesUnchanged()
    {
        var data = ShortsToBytes(1000, -2000, 16000);
        var original = (byte[])data.Clone();

        PcmGain.Apply(data, data.Length, bitsPerSample: 16, gain: 1f);

        Assert.Equal(original, data);
    }

    [Fact]
    public void Apply_GainZero_SilencesBuffer()
    {
        var data = ShortsToBytes(1000, -2000, short.MaxValue);

        PcmGain.Apply(data, data.Length, bitsPerSample: 16, gain: 0f);

        Assert.All(data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Apply_GainTwo_BoostsAndClamps()
    {
        var data = ShortsToBytes(1000, -2000, 20000);

        PcmGain.Apply(data, data.Length, bitsPerSample: 16, gain: 2f);

        var samples = BytesToShorts(data);
        Assert.Equal(2000, samples[0]);
        Assert.Equal(-4000, samples[1]);
        Assert.Equal(short.MaxValue, samples[2]); // 40000 would wrap without clamp
    }

    [Fact]
    public void Apply_GainHalf_Attenuates()
    {
        var data = ShortsToBytes(1000, -2000);

        PcmGain.Apply(data, data.Length, bitsPerSample: 16, gain: 0.5f);

        var samples = BytesToShorts(data);
        Assert.Equal(500, samples[0]);
        Assert.Equal(-1000, samples[1]);
    }

    private static byte[] ShortsToBytes(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private static short[] BytesToShorts(byte[] data)
    {
        var samples = new short[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
        }

        return samples;
    }
}
