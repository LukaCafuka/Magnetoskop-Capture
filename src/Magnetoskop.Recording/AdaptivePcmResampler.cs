using NAudio.Dsp;

namespace Magnetoskop.Recording;

/// <summary>
/// Stateful interleaved-PCM resampler backed by Cockos WDL. The nominal input rate is
/// adjusted by a slowly changing ppm correction while the emitted format remains fixed.
/// </summary>
public sealed class AdaptivePcmResampler
{
    private readonly WdlResampler _resampler = new();
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly int _bytesPerSample;
    private readonly double _clampPpm;
    private readonly double _slewPpmPerSecond;
    private double _appliedPpm;
    private bool _flushed;

    public AdaptivePcmResampler(
        int sampleRate,
        int channels,
        int bitsPerSample,
        double clampPpm = 1000,
        double slewPpmPerSecond = 50)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample is not (16 or 24 or 32))
            throw new NotSupportedException($"{bitsPerSample}-bit PCM is not supported.");

        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _bytesPerSample = bitsPerSample / 8;
        _clampPpm = clampPpm;
        _slewPpmPerSecond = slewPpmPerSecond;

        _resampler.SetMode(interp: true, filtercnt: 2, sinc: true, sinc_size: 64, sinc_interpsize: 32);
        _resampler.SetFeedMode(wantInputDriven: true);
        ApplyRate();
    }

    public double AppliedCorrectionPpm => _appliedPpm;

    /// <summary>Moves the active ratio toward the requested correction at the configured slew.</summary>
    public double SetTargetCorrection(double targetPpm, TimeSpan elapsed)
    {
        targetPpm = Math.Clamp(targetPpm, -_clampPpm, _clampPpm);
        var maxDelta = Math.Max(0, elapsed.TotalSeconds) * _slewPpmPerSecond;
        var delta = targetPpm - _appliedPpm;
        _appliedPpm = Math.Abs(delta) <= maxDelta
            ? targetPpm
            : _appliedPpm + Math.CopySign(maxDelta, delta);
        ApplyRate();
        return _appliedPpm;
    }

    /// <summary>Resamples one complete interleaved PCM block.</summary>
    public byte[] Process(ReadOnlySpan<byte> pcm)
    {
        if (_flushed) throw new InvalidOperationException("The resampler was already flushed.");
        var blockAlign = _channels * _bytesPerSample;
        if (pcm.Length % blockAlign != 0)
            throw new ArgumentException("PCM buffer length is not aligned to complete sample frames.", nameof(pcm));
        if (pcm.IsEmpty) return Array.Empty<byte>();

        var inputFrames = pcm.Length / blockAlign;
        var requested = _resampler.ResamplePrepare(inputFrames, _channels,
            out var inputBuffer, out var inputOffset);
        if (requested < inputFrames)
            throw new InvalidOperationException("WDL resampler accepted fewer input frames than supplied.");

        ConvertToFloat(pcm, inputBuffer.AsSpan(inputOffset, inputFrames * _channels));

        // The ratio is bounded to ±1000 ppm, but include WDL filter latency and headroom.
        var maxOutputFrames = inputFrames + 256;
        var output = new float[maxOutputFrames * _channels];
        var generatedFrames = _resampler.ResampleOut(
            output, 0, inputFrames, maxOutputFrames, _channels);
        return ConvertFromFloat(output.AsSpan(0, generatedFrames * _channels));
    }

    /// <summary>
    /// Emits WDL's remaining valid filter-delay samples. Passing fewer input samples
    /// than prepared is WDL's native flush operation; no zero/silence PCM is inserted.
    /// </summary>
    public byte[] Flush()
    {
        if (_flushed) return Array.Empty<byte>();
        _flushed = true;

        const int flushRequestFrames = 512;
        var requested = _resampler.ResamplePrepare(
            flushRequestFrames, _channels, out _, out _);
        var maxOutputFrames = Math.Max(flushRequestFrames, requested) + 512;
        var output = new float[maxOutputFrames * _channels];
        var generatedFrames = _resampler.ResampleOut(
            output, 0, 0, maxOutputFrames, _channels);
        return ConvertFromFloat(output.AsSpan(0, generatedFrames * _channels));
    }

    private void ApplyRate()
    {
        var correctedInputRate = _sampleRate * (1.0 + _appliedPpm / 1_000_000.0);
        _resampler.SetRates(correctedInputRate, _sampleRate);
    }

    private void ConvertToFloat(ReadOnlySpan<byte> input, Span<float> output)
    {
        switch (_bitsPerSample)
        {
            case 16:
                for (var i = 0; i < output.Length; i++)
                {
                    var offset = i * 2;
                    var value = (short)(input[offset] | input[offset + 1] << 8);
                    output[i] = value / 32768f;
                }
                break;
            case 24:
                for (var i = 0; i < output.Length; i++)
                {
                    var offset = i * 3;
                    var value = input[offset] | input[offset + 1] << 8 | input[offset + 2] << 16;
                    if ((value & 0x0080_0000) != 0) value |= unchecked((int)0xFF00_0000);
                    output[i] = value / 8388608f;
                }
                break;
            case 32:
                for (var i = 0; i < output.Length; i++)
                {
                    var offset = i * 4;
                    var value = input[offset]
                        | input[offset + 1] << 8
                        | input[offset + 2] << 16
                        | input[offset + 3] << 24;
                    output[i] = value / 2147483648f;
                }
                break;
        }
    }

    private byte[] ConvertFromFloat(ReadOnlySpan<float> input)
    {
        var output = new byte[input.Length * _bytesPerSample];
        switch (_bitsPerSample)
        {
            case 16:
                for (var i = 0; i < input.Length; i++)
                {
                    var value = (int)MathF.Round(Math.Clamp(input[i], -1f, 32767f / 32768f) * 32768f);
                    var offset = i * 2;
                    output[offset] = (byte)value;
                    output[offset + 1] = (byte)(value >> 8);
                }
                break;
            case 24:
                for (var i = 0; i < input.Length; i++)
                {
                    var value = (int)MathF.Round(Math.Clamp(input[i], -1f, 8388607f / 8388608f) * 8388608f);
                    var offset = i * 3;
                    output[offset] = (byte)value;
                    output[offset + 1] = (byte)(value >> 8);
                    output[offset + 2] = (byte)(value >> 16);
                }
                break;
            case 32:
                for (var i = 0; i < input.Length; i++)
                {
                    var scaled = Math.Clamp(
                        (double)input[i], -1.0, 2147483647.0 / 2147483648.0) * 2147483648.0;
                    var value = (int)Math.Round(scaled);
                    var offset = i * 4;
                    output[offset] = (byte)value;
                    output[offset + 1] = (byte)(value >> 8);
                    output[offset + 2] = (byte)(value >> 16);
                    output[offset + 3] = (byte)(value >> 24);
                }
                break;
        }
        return output;
    }
}
