using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Magnetoskop.Capture.Audio;

/// <summary>
/// Audio capture over NAudio WASAPI. Converts the endpoint's mix format to
/// 16-bit PCM, publishes timestamped buffers to bounded subscriber channels,
/// and maintains per-channel peak levels for UI metering.
/// </summary>
public sealed class NAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<NAudioCaptureService> _logger;
    private readonly List<CaptureSubscription<AudioBuffer>> _subscribers = new();
    private readonly object _gate = new();
    private readonly object _healthGate = new();

    private WasapiPacketCapture? _capture;
    private AudioFormat? _format;
    private float[] _peaks = new float[2];
    private CaptureHealth _health = new()
    {
        State = CaptureHealthState.Stopped,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
    };

    public NAudioCaptureService(ILogger<NAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public AudioFormat? CurrentFormat => _format;

    public CaptureHealth Health
    {
        get
        {
            lock (_healthGate) return _health;
        }
    }

    public event EventHandler<CaptureHealthEventArgs>? HealthChanged;

    public IReadOnlyList<float> PeakLevels
    {
        get
        {
            lock (_gate)
            {
                var copy = (float[])_peaks.Clone();
                Array.Clear(_peaks);
                return copy;
            }
        }
    }

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CaptureDeviceInfo>>(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception)
            {
                // no default capture endpoint present
            }

            var devices = new List<CaptureDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new CaptureDeviceInfo
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        IsDefault = device.ID == defaultId,
                    });
                }
            }
            return devices;
        }, cancellationToken);
    }

    public async Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(
        CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateDevicesAsync(cancellationToken);
        if (devices.Count == 0) return null;

        // Score by shared word tokens between the video and audio device names
        // (capture cards typically expose e.g. "XYZ Video Capture" + "XYZ Audio Capture").
        static HashSet<string> Tokens(string name) => name
            .Split(new[] { ' ', '-', '_', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        var videoTokens = Tokens(videoDevice.Name);
        var best = devices
            .Select(d => (Device: d, Score: Tokens(d.Name).Intersect(videoTokens).Count()))
            .OrderByDescending(x => x.Score)
            .First();

        return best.Score > 0
            ? best.Device
            : devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (IsCapturing)
        {
            throw new InvalidOperationException("Audio capture is already running.");
        }

        SetHealth(new CaptureHealth
        {
            State = CaptureHealthState.Starting,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        });

        return Task.Run(() =>
        {
            WasapiPacketCapture? capture = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var mmDevice = enumerator.GetDevice(device.Id)
                    ?? throw new IOException($"Audio device '{device.Name}' not found.");

                // Request 16-bit PCM at the endpoint's engine rate. The low-level
                // reader uses shared-mode AutoConvertPcm and retains native packet
                // device/QPC positions that WasapiCapture.DataAvailable discards.
                using var formatProbe = mmDevice.AudioClient;
                var mixFormat = formatProbe.MixFormat;
                var waveFormat = new WaveFormat(mixFormat.SampleRate,
                    16, Math.Min(2, mixFormat.Channels));
                capture = new WasapiPacketCapture(mmDevice, waveFormat);

                var format = new AudioFormat
                {
                    SampleRate = capture.WaveFormat.SampleRate,
                    Channels = capture.WaveFormat.Channels,
                    BitsPerSample = capture.WaveFormat.BitsPerSample,
                };

                lock (_gate)
                {
                    _peaks = new float[format.Channels];
                }

                capture.PacketAvailable += (_, e) => OnPacketAvailable(e, format);
                capture.CaptureStopped += (_, e) => OnCaptureStopped(e.Exception);

                _capture = capture;
                _format = format;
                IsCapturing = true;
                capture.Start();

                SetHealth(new CaptureHealth
                {
                    State = CaptureHealthState.Running,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                });

                _logger.LogInformation("Audio capture started: {Device} {Rate} Hz, {Channels} ch, {Bits} bit",
                    device.Name, format.SampleRate, format.Channels, format.BitsPerSample);
            }
            catch (Exception ex)
            {
                capture?.Dispose();
                _capture = null;
                _format = null;
                IsCapturing = false;
                var health = Health;
                SetHealth(health with
                {
                    State = CaptureHealthState.Faulted,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                    TotalCaptureFailures = health.TotalCaptureFailures + 1,
                    ConsecutiveCaptureFailures = 1,
                    Error = ex.Message,
                });
                throw;
            }
        }, cancellationToken);
    }

    private void OnPacketAvailable(WasapiPacketEventArgs packet, AudioFormat format)
    {
        if (packet.SampleCount == 0) return;

        var data = packet.Data;

        UpdatePeaks(data, format);

        var discontinuity = (packet.Flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
        var silent = (packet.Flags & AudioClientBufferFlags.Silent) != 0;
        var timestampError = (packet.Flags & AudioClientBufferFlags.TimestampError) != 0;

        // WASAPI returns QPC already converted to 100 ns units. If the driver marks
        // it invalid, safely estimate the first-sample time from host receipt minus
        // packet duration, and make that lower-quality provenance explicit.
        var timestampEstimated = timestampError || packet.QpcPosition100ns <= 0;
        var timestamp100ns = timestampEstimated
            ? CaptureMonotonicClock.GetTimestamp100ns()
              - (long)packet.SampleCount * TimeSpan.TicksPerSecond / format.SampleRate
            : packet.QpcPosition100ns;

        var buffer = new AudioBuffer
        {
            Data = data,
            Length = data.Length,
            Format = format,
            Timestamp100ns = timestamp100ns,
            FirstSampleIndex = packet.DevicePosition,
            SampleCount = packet.SampleCount,
            QpcPosition100ns = packet.QpcPosition100ns,
            DevicePosition = packet.DevicePosition,
            Discontinuity = discontinuity,
            TimestampEstimated = timestampEstimated,
            Silent = silent,
        };

        CaptureSubscription<AudioBuffer>[] subscribers;
        lock (_gate)
        {
            subscribers = _subscribers
                .OrderBy(subscription => subscription.Options.OverflowPolicy
                    == CaptureOverflowPolicy.RejectNew ? 0 : 1)
                .ToArray();
        }
        foreach (var sub in subscribers)
        {
            sub.TryPublish(buffer);
        }

        NoteDelivery(timestamp100ns, discontinuity);
    }

    private void UpdatePeaks(byte[] data, AudioFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var frameBytes = bytesPerSample * format.Channels;

        lock (_gate)
        {
            for (var offset = 0; offset + frameBytes <= data.Length; offset += frameBytes)
            {
                for (var ch = 0; ch < format.Channels; ch++)
                {
                    var sample = BitConverter.ToInt16(data, offset + ch * bytesPerSample);
                    var level = Math.Abs(sample / (float)short.MaxValue);
                    if (level > _peaks[ch]) _peaks[ch] = level;
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is null)
        {
            IsCapturing = false;
            _format = null;
            CompleteSubscriptions();
            if (Health.State != CaptureHealthState.Stopped)
            {
                SetHealth(Health with
                {
                    State = CaptureHealthState.Stopped,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                    ConsecutiveCaptureFailures = 0,
                });
            }
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var capture = _capture;
            _capture = null;
            IsCapturing = false;
            try
            {
                capture.Stop();
            }
            finally
            {
                capture.Dispose();
                _format = null;
                lock (_gate)
                {
                    Array.Clear(_peaks);
                }
                CompleteSubscriptions();
                SetHealth(Health with
                {
                    State = CaptureHealthState.Stopped,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                    ConsecutiveCaptureFailures = 0,
                });
                _logger.LogInformation("Audio capture stopped");
            }
        }, CancellationToken.None);
    }

    public CaptureSubscription<AudioBuffer> Subscribe(int capacity = 16)
        => Subscribe(CaptureSubscriptionOptions.Monitor(capacity));

    public CaptureSubscription<AudioBuffer> Subscribe(CaptureSubscriptionOptions options)
    {
        var subscription = new CaptureSubscription<AudioBuffer>(
            options,
            singleWriter: true,
            onDisposed: RemoveSubscription,
            onOverflow: OnSubscriptionOverflow);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }
        return subscription;
    }

    private void OnCaptureStopped(Exception? exception)
    {
        if (exception is null) return;

        _logger.LogError(exception, "Audio capture stopped due to an error");
        IsCapturing = false;
        var health = Health;
        SetHealth(health with
        {
            State = CaptureHealthState.Faulted,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
            TotalCaptureFailures = health.TotalCaptureFailures + 1,
            ConsecutiveCaptureFailures = health.ConsecutiveCaptureFailures + 1,
            Error = exception.Message,
        });
        CompleteSubscriptions(exception);
    }

    private void RemoveSubscription(CaptureSubscription<AudioBuffer> subscription)
    {
        lock (_gate) _subscribers.Remove(subscription);
    }

    private void OnSubscriptionOverflow(CaptureOverflow _)
    {
        var health = Health;
        SetHealth(health with
        {
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
            SubscriberOverflows = health.SubscriberOverflows + 1,
        });
    }

    private void CompleteSubscriptions(Exception? error = null)
    {
        CaptureSubscription<AudioBuffer>[] subscribers;
        lock (_gate)
        {
            subscribers = _subscribers.ToArray();
            _subscribers.Clear();
        }
        foreach (var subscription in subscribers) subscription.Complete(error);
    }

    private void NoteDelivery(long timestamp100ns, bool discontinuity)
    {
        CaptureHealth updated;
        lock (_healthGate)
        {
            updated = _health with
            {
                State = CaptureHealthState.Running,
                Timestamp100ns = timestamp100ns,
                LastDeliveryTimestamp100ns = timestamp100ns,
                ItemsDelivered = _health.ItemsDelivered + 1,
                ConsecutiveCaptureFailures = 0,
                SourceDiscontinuities = _health.SourceDiscontinuities + (discontinuity ? 1 : 0),
                Error = null,
            };
            _health = updated;
        }
        if (discontinuity) RaiseHealthChanged(updated);
    }

    private void SetHealth(CaptureHealth health)
    {
        lock (_healthGate) _health = health;
        RaiseHealthChanged(health);
    }

    private void RaiseHealthChanged(CaptureHealth health)
    {
        try { HealthChanged?.Invoke(this, new CaptureHealthEventArgs(health)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Audio capture health observer failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        CompleteSubscriptions();
    }
}
