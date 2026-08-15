using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Simulation;

/// <summary>
/// Generates SMPTE-style color bars with a moving element so the preview pipeline
/// can be exercised without a capture device.
/// </summary>
public sealed class SimulatedVideoCaptureService : IVideoCaptureService, IConfigurableVideoCaptureService
{
    private static readonly VideoFormat PalFormat = new()
    {
        Width = 720,
        Height = 576,
        FrameRate = 25,
        PixelFormat = VideoPixelFormat.Bgr24,
        Interlaced = true,
        TopFieldFirst = true,
    };

    // BGR colors of the classic 75% color bars.
    private static readonly (byte B, byte G, byte R)[] Bars =
    {
        (191, 191, 191), // white
        (0, 191, 191),   // yellow
        (191, 191, 0),   // cyan
        (0, 191, 0),     // green
        (191, 0, 191),   // magenta
        (0, 0, 191),     // red
        (191, 0, 0),     // blue
        (0, 0, 0),       // black
    };

    private readonly ILogger<SimulatedVideoCaptureService> _logger;
    private readonly List<CaptureSubscription<VideoFrame>> _subscribers = new();
    private readonly object _gate = new();
    private readonly object _healthGate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private CaptureHealth _health = new()
    {
        State = CaptureHealthState.Stopped,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
    };

    public SimulatedVideoCaptureService(ILogger<SimulatedVideoCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public VideoFormat? CurrentFormat => IsCapturing ? PalFormat : null;

    public VideoInputConfiguration InputConfiguration
    {
        get => VideoInputConfiguration.SimulatedPalTff;
        set { /* the deterministic simulator is intentionally fixed at PAL TFF */ }
    }

    public VideoInputFormatStatus FormatStatus { get; }
        = VideoInputFormatStatus.FromReadback(
            VideoInputConfiguration.SimulatedPalTff,
            PalFormat,
            scanReadbackAvailable: true);

    public CaptureHealth Health
    {
        get
        {
            lock (_healthGate) return _health;
        }
    }

    public event EventHandler<CaptureHealthEventArgs>? HealthChanged;

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CaptureDeviceInfo> devices = new[]
        {
            new CaptureDeviceInfo { Id = "sim:bars", Name = "Simulated PAL source (color bars)", IsDefault = true },
        };
        return Task.FromResult(devices);
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
        _logger.LogInformation("Simulated video capture started ({Device})", device.Name);
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
        CompleteSubscriptions();
        SetHealth(Health with
        {
            State = CaptureHealthState.Stopped,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        });
        _logger.LogInformation("Simulated video capture stopped");
    }

    public CaptureSubscription<VideoFrame> Subscribe(int capacity = 4)
        => Subscribe(CaptureSubscriptionOptions.Preview(capacity));

    public CaptureSubscription<VideoFrame> Subscribe(CaptureSubscriptionOptions options)
    {
        var subscription = new CaptureSubscription<VideoFrame>(
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
            _logger.LogError(ex, "Simulated video capture failed");
        }
    }

    private async Task GenerateAsync(CancellationToken ct)
    {
        var format = PalFormat;
        long frameNumber = 0;
        var framePeriod = TimeSpan.FromSeconds(1.0 / format.FrameRate);

        using var timer = new PeriodicTimer(framePeriod);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns();
            var frame = RenderFrame(format, frameNumber, timestamp100ns);
            frameNumber++;

            CaptureSubscription<VideoFrame>[] subscribers;
            lock (_gate)
            {
                subscribers = _subscribers
                    .OrderBy(subscription => subscription.Options.OverflowPolicy
                        == CaptureOverflowPolicy.RejectNew ? 0 : 1)
                    .ToArray();
            }
            foreach (var sub in subscribers)
            {
                sub.TryPublish(frame);
            }
            NoteDelivery(timestamp100ns);
        }
    }

    private static VideoFrame RenderFrame(VideoFormat format, long frameNumber, long timestamp100ns)
    {
        var data = new byte[format.BytesPerFrame];
        var barWidth = format.Width / Bars.Length;

        for (var y = 0; y < format.Height; y++)
        {
            var rowOffset = y * format.Width * 3;
            for (var x = 0; x < format.Width; x++)
            {
                var bar = Math.Min(x / barWidth, Bars.Length - 1);
                var (b, g, r) = Bars[bar];
                var i = rowOffset + x * 3;
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
            }
        }

        // Moving white block along the bottom to make motion visible.
        var blockX = (int)(frameNumber * 4 % Math.Max(1, format.Width - 40));
        for (var y = format.Height - 40; y < format.Height - 8; y++)
        {
            var rowOffset = y * format.Width * 3;
            for (var x = blockX; x < blockX + 40 && x < format.Width; x++)
            {
                var i = rowOffset + x * 3;
                data[i] = 255;
                data[i + 1] = 255;
                data[i + 2] = 255;
            }
        }

        return new VideoFrame
        {
            Data = data,
            Format = format,
            Timestamp100ns = timestamp100ns,
            DeliverySequence = frameNumber,
            FrameNumber = frameNumber,
        };
    }

    private void RemoveSubscription(CaptureSubscription<VideoFrame> subscription)
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
        CaptureSubscription<VideoFrame>[] subscriptions;
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
        catch (Exception ex) { _logger.LogWarning(ex, "Simulated video health observer failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        CompleteSubscriptions();
    }
}
