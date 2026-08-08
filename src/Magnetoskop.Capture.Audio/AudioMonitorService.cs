using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Magnetoskop.Capture.Audio;

/// <summary>
/// Optional headphone/speaker monitoring of the live <see cref="IAudioCaptureService"/> stream
/// via WASAPI shared-mode output. Never stalls capture or recording.
/// </summary>
public sealed class AudioMonitorService : IAsyncDisposable
{
    private readonly IAudioCaptureService _audio;
    private readonly ILogger<AudioMonitorService> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private bool _wantEnabled;
    private float _volume = 1f;

    /// <summary>Max buffered audio before we discard to keep latency low.</summary>
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(200);

    public AudioMonitorService(IAudioCaptureService audio, ILogger<AudioMonitorService> logger)
    {
        _audio = audio;
        _logger = logger;
    }

    /// <summary>
    /// Linear monitor gain 0–2 (0%–200%). Applied in software so boost above 100% works.
    /// Does not affect recording. Updates take effect on the next pumped chunk.
    /// </summary>
    public float Volume
    {
        get
        {
            lock (_gate) return _volume;
        }
        set
        {
            var clamped = Math.Clamp(value, 0f, 2f);
            lock (_gate) _volume = clamped;
        }
    }

    /// <summary>
    /// Align playback with the checkbox and current capture state.
    /// Starts monitoring when enabled and capture is running; otherwise stops it.
    /// </summary>
    public Task SyncAsync(bool wantEnabled)
    {
        lock (_gate)
        {
            _wantEnabled = wantEnabled;
        }

        try
        {
            if (wantEnabled && _audio.IsCapturing && _audio.CurrentFormat is { } format)
            {
                StartPlayback_NoThrow(format);
            }
            else
            {
                StopPlayback_NoThrow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio monitor sync failed");
            StopPlayback_NoThrow();
        }

        return Task.CompletedTask;
    }

    private void StartPlayback_NoThrow(AudioFormat format)
    {
        StopPlayback_NoThrow();

        try
        {
            var waveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            var buffer = new BufferedWaveProvider(waveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(500),
            };

            var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
            output.Init(buffer);
            output.Play();

            var cts = new CancellationTokenSource();
            var reader = _audio.Subscribe(capacity: 8);
            var bits = format.BitsPerSample;
            var pump = Task.Run(() => PumpAsync(reader, buffer, bits, cts.Token), CancellationToken.None);

            lock (_gate)
            {
                _buffer = buffer;
                _output = output;
                _cts = cts;
                _pumpTask = pump;
            }

            _logger.LogInformation(
                "Audio monitoring started ({Rate} Hz, {Ch} ch)",
                format.SampleRate, format.Channels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start audio monitoring");
            StopPlayback_NoThrow();
        }
    }

    private async Task PumpAsync(
        ChannelReader<AudioBuffer> reader,
        BufferedWaveProvider buffer,
        int bitsPerSample,
        CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                if (buffer.BufferedDuration > MaxBuffered)
                {
                    buffer.ClearBuffer();
                }

                float gain;
                lock (_gate) gain = _volume;

                if (Math.Abs(gain - 1f) >= 0.0001f)
                {
                    // Capture buffers may be shared with recording/meters — copy before gain.
                    var copy = new byte[chunk.Length];
                    Buffer.BlockCopy(chunk.Data, 0, copy, 0, chunk.Length);
                    PcmGain.Apply(copy, chunk.Length, bitsPerSample, gain);
                    buffer.AddSamples(copy, 0, chunk.Length);
                }
                else
                {
                    buffer.AddSamples(chunk.Data, 0, chunk.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio monitor pump ended");
        }
    }

    private void StopPlayback_NoThrow()
    {
        CancellationTokenSource? cts;
        Task? pump;
        WasapiOut? output;

        lock (_gate)
        {
            cts = _cts;
            pump = _pumpTask;
            output = _output;
            _cts = null;
            _pumpTask = null;
            _output = null;
            _buffer = null;
        }

        try { cts?.Cancel(); } catch (Exception) { /* ignore */ }

        try
        {
            if (pump is not null)
            {
                pump.Wait(TimeSpan.FromMilliseconds(500));
            }
        }
        catch (Exception) { /* ignore */ }

        try
        {
            output?.Stop();
            output?.Dispose();
        }
        catch (Exception) { /* ignore */ }

        try { cts?.Dispose(); } catch (Exception) { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        await SyncAsync(false);
    }
}
