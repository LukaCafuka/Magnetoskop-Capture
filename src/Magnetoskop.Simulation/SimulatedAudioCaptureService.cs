using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Simulation;

/// <summary>Generates a stereo 1 kHz sine tone so the audio pipeline can run without hardware.</summary>
public sealed class SimulatedAudioCaptureService : IAudioCaptureService
{
    private static readonly AudioFormat Format = new()
    {
        SampleRate = 48000,
        Channels = 2,
        BitsPerSample = 16,
    };

    private readonly ILogger<SimulatedAudioCaptureService> _logger;
    private readonly List<CaptureSubscription<AudioBuffer>> _subscribers = new();
    private readonly object _gate = new();
    private readonly object _healthGate = new();
    private readonly float[] _peaks = new float[2];

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private CaptureHealth _health = new()
    {
        State = CaptureHealthState.Stopped,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
    };

    public SimulatedAudioCaptureService(ILogger<SimulatedAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public AudioFormat? CurrentFormat => IsCapturing ? Format : null;

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
        IReadOnlyList<CaptureDeviceInfo> devices = new[]
        {
            new CaptureDeviceInfo { Id = "sim:tone", Name = "Simulated audio source (1 kHz tone)", IsDefault = true },
        };
        return Task.FromResult(devices);
    }

    public async Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateDevicesAsync(cancellationToken);
        return devices.FirstOrDefault();
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (IsCapturing) return Task.CompletedTask;

        IsCapturing = true;
        _cts = new CancellationTokenSource();
        SetHealth(new CaptureHealth
        {
            State = CaptureHealthState.Running,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        });
        _loop = Task.Run(() => RunGenerationAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Simulated audio capture started ({Device})", device.Name);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing && _cts is null && _loop is null)
        {
            CompleteSubscriptions();
            return;
        }

        IsCapturing = false;
        if (_cts is not null) await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        lock (_gate)
        {
            Array.Clear(_peaks);
        }
        CompleteSubscriptions();
        SetHealth(Health with
        {
            State = CaptureHealthState.Stopped,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        });
        _logger.LogInformation("Simulated audio capture stopped");
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

    private async Task RunGenerationAsync(CancellationToken ct)
    {
        try
        {
            await GenerateAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal stop
        }
        catch (Exception ex)
        {
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
            CompleteSubscriptions(ex);
            _logger.LogError(ex, "Simulated audio capture failed");
        }
    }

    private async Task GenerateAsync(CancellationToken ct)
    {
        const double frequencyHz = 1000.0;
        const double amplitude = 0.5;
        const int blockMs = 20;
        var samplesPerBlock = Format.SampleRate * blockMs / 1000;
        var bytesPerBlock = samplesPerBlock * Format.Channels * (Format.BitsPerSample / 8);

        long sampleIndex = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(blockMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var data = new byte[bytesPerBlock];
            float peak = 0;

            for (var i = 0; i < samplesPerBlock; i++)
            {
                var t = (double)(sampleIndex + i) / Format.SampleRate;
                var value = amplitude * Math.Sin(2 * Math.PI * frequencyHz * t);
                var sample = (short)(value * short.MaxValue);
                peak = Math.Max(peak, Math.Abs((float)value));

                var offset = i * Format.Channels * 2;
                // Left + right (identical).
                data[offset] = (byte)(sample & 0xFF);
                data[offset + 1] = (byte)(sample >> 8 & 0xFF);
                data[offset + 2] = data[offset];
                data[offset + 3] = data[offset + 1];
            }

            lock (_gate)
            {
                if (peak > _peaks[0]) _peaks[0] = peak;
                if (peak > _peaks[1]) _peaks[1] = peak;
            }

            var buffer = new AudioBuffer
            {
                Data = data,
                Length = data.Length,
                Format = Format,
                Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns()
                    - (long)samplesPerBlock * TimeSpan.TicksPerSecond / Format.SampleRate,
                FirstSampleIndex = sampleIndex,
                SampleCount = samplesPerBlock,
                QpcPosition100ns = 0,
                DevicePosition = sampleIndex,
                Discontinuity = false,
                TimestampEstimated = true,
                Silent = false,
            };
            sampleIndex += samplesPerBlock;

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
            NoteDelivery(buffer.Timestamp100ns);
        }
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
        CaptureSubscription<AudioBuffer>[] subscriptions;
        lock (_gate)
        {
            subscriptions = _subscribers.ToArray();
            _subscribers.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Complete(error);
    }

    private void NoteDelivery(long timestamp100ns)
    {
        lock (_healthGate)
        {
            _health = _health with
            {
                State = CaptureHealthState.Running,
                Timestamp100ns = timestamp100ns,
                LastDeliveryTimestamp100ns = timestamp100ns,
                ItemsDelivered = _health.ItemsDelivered + 1,
            };
        }
    }

    private void SetHealth(CaptureHealth health)
    {
        lock (_healthGate) _health = health;
        try { HealthChanged?.Invoke(this, new CaptureHealthEventArgs(health)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Simulated audio health observer failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        CompleteSubscriptions();
    }
}
