using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
    private VolumeSampleProvider? _volumeProvider;
    private AudioFormat? _runningFormat;
    private bool _wantEnabled;
    private float _volume = 1f;

    /// <summary>
    /// If the playback queue grows past this, discard it so A/V stay roughly in sync.
    /// Kept low: a 200–500 ms queue is what made monitoring feel suddenly delayed.
    /// </summary>
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(60);

    /// <summary>Hard cap on the NAudio buffer (overflow drops new samples).</summary>
    private static readonly TimeSpan BufferCapacity = TimeSpan.FromMilliseconds(100);

    /// <summary>WASAPI shared-mode engine period hint (ms).</summary>
    private const int WasapiLatencyMs = 30;

    public AudioMonitorService(IAudioCaptureService audio, ILogger<AudioMonitorService> logger)
    {
        _audio = audio;
        _logger = logger;
    }

    /// <summary>
    /// Linear monitor gain 0–2 (0%–200%). Applied via <see cref="VolumeSampleProvider"/>
    /// so boost above 100% does not allocate on the capture pump. Does not affect recording.
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
            lock (_gate)
            {
                _volume = clamped;
                if (_volumeProvider is not null)
                    _volumeProvider.Volume = clamped;
            }
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
        lock (_gate)
        {
            // Avoid tear-down/restart when already monitoring this format (checkbox/bindings
            // can fire SyncAsync repeatedly and each restart refilled a large buffer).
            if (_output is not null
                && _runningFormat is { } running
                && running.SampleRate == format.SampleRate
                && running.Channels == format.Channels
                && running.BitsPerSample == format.BitsPerSample)
            {
                return;
            }
        }

        StopPlayback_NoThrow();

        try
        {
            var waveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            var buffer = new BufferedWaveProvider(waveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = BufferCapacity,
            };

            float volume;
            lock (_gate) volume = _volume;

            // Gain in the pull path — no per-chunk byte[] copies on the pump.
            var volumeProvider = new VolumeSampleProvider(buffer.ToSampleProvider())
            {
                Volume = volume,
            };

            var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, WasapiLatencyMs);
            output.Init(volumeProvider);
            output.Play();

            var cts = new CancellationTokenSource();
            var reader = _audio.Subscribe(capacity: 4);
            var pump = Task.Run(() => PumpAsync(reader, buffer, cts.Token), CancellationToken.None);

            lock (_gate)
            {
                _buffer = buffer;
                _volumeProvider = volumeProvider;
                _output = output;
                _cts = cts;
                _pumpTask = pump;
                _runningFormat = format;
            }

            _logger.LogInformation(
                "Audio monitoring started ({Rate} Hz, {Ch} ch, latency target ≤{Ms} ms)",
                format.SampleRate, format.Channels, MaxBuffered.TotalMilliseconds);
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
        CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                // Drop backlog before enqueue so we never sit on a growing delay.
                if (buffer.BufferedDuration > MaxBuffered)
                {
                    buffer.ClearBuffer();
                    _logger.LogDebug(
                        "Audio monitor cleared playback queue (was >{Ms} ms)",
                        MaxBuffered.TotalMilliseconds);
                }

                buffer.AddSamples(chunk.Data, 0, chunk.Length);

                if (buffer.BufferedDuration > MaxBuffered)
                {
                    buffer.ClearBuffer();
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
            _volumeProvider = null;
            _runningFormat = null;
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
