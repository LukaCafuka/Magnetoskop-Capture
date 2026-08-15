using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Magnetoskop.Capture.Video;

/// <summary>
/// Video capture over OpenCvSharp (DirectShow backend). A dedicated background
/// thread grabs frames from <see cref="VideoCapture"/> and fans them out to bounded
/// per-subscriber channels (drop-oldest), so slow consumers never stall capture.
/// </summary>
public sealed class OpenCvVideoCaptureService : IVideoCaptureService, IConfigurableVideoCaptureService
{
    private readonly ILogger<OpenCvVideoCaptureService> _logger;
    private readonly List<CaptureSubscription<VideoFrame>> _subscribers = new();
    private readonly object _gate = new();
    private readonly object _healthGate = new();

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private VideoFormat? _format;
    private VideoInputConfiguration _inputConfiguration = new();
    private VideoInputFormatStatus _formatStatus;
    private CaptureHealth _health = new()
    {
        State = CaptureHealthState.Stopped,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
    };

    public OpenCvVideoCaptureService(ILogger<OpenCvVideoCaptureService> logger)
    {
        _logger = logger;
        _formatStatus = VideoInputFormatStatus.Pending(_inputConfiguration);
    }

    public bool IsCapturing { get; private set; }

    public VideoFormat? CurrentFormat => _format;

    public VideoInputConfiguration InputConfiguration
    {
        get => _inputConfiguration;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (IsCapturing)
            {
                throw new InvalidOperationException("Video input configuration cannot change while capturing.");
            }
            _inputConfiguration = value;
            _formatStatus = VideoInputFormatStatus.Pending(value);
        }
    }

    public VideoInputFormatStatus FormatStatus => _formatStatus;

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
        return Task.Run<IReadOnlyList<CaptureDeviceInfo>>(() =>
        {
            try
            {
                var enumerated = DirectShowDeviceEnumerator.Enumerate();
                if (enumerated.Count > 0) return enumerated;

                _logger.LogWarning(
                    "DirectShow returned no enumerated devices; falling back to index probing");
                return ProbeByIndex();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DirectShow enumeration failed; falling back to index probing");
                return ProbeByIndex();
            }
        }, cancellationToken);
    }

    private static IReadOnlyList<CaptureDeviceInfo> ProbeByIndex(int max = 4)
    {
        var found = new List<CaptureDeviceInfo>();
        for (var i = 0; i < max; i++)
        {
            using var probe = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
            if (probe.IsOpened())
            {
                found.Add(new CaptureDeviceInfo
                {
                    Id = i.ToString(),
                    Name = $"Video device {i}",
                    IsDefault = i == 0,
                });
            }
        }
        return found;
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (IsCapturing)
        {
            throw new InvalidOperationException("Video capture is already running.");
        }
        var index = int.TryParse(device.Id, out var legacyIndex)
            ? legacyIndex
            : DirectShowDeviceEnumerator.ResolveCurrentIndex(device.Id);
        if (index is null)
        {
            throw new IOException(
                $"Video capture device '{device.Name}' is no longer present (stable id '{device.Id}').");
        }

        SetHealth(new CaptureHealth
        {
            State = CaptureHealthState.Starting,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        });

        return Task.Run(() =>
        {
            VideoCapture? capture = null;
            try
            {
                capture = new VideoCapture(index.Value, VideoCaptureAPIs.DSHOW);
                if (!capture.IsOpened())
                {
                    capture.Dispose();
                    capture = null;
                    throw new IOException($"Cannot open video capture device '{device.Name}' (index {index.Value}).");
                }

                var configuration = _inputConfiguration;
                ApplyRequestedFormat(capture, configuration);

                var width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
                var height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                var fps = capture.Get(VideoCaptureProperties.Fps);
                if (fps <= 0 || double.IsNaN(fps)) fps = 25.0; // PAL default when the driver does not report

                _format = new VideoFormat
                {
                    Width = width,
                    Height = height,
                    FrameRate = fps,
                    PixelFormat = VideoPixelFormat.Bgr24,
                    // Explicit operator selection is authoritative. The legacy
                    // height/TFF fallback remains only for migrated unspecified settings.
                    Interlaced = configuration.ScanMode == VideoScanMode.Unspecified
                        ? height >= 480
                        : configuration.Interlaced,
                    TopFieldFirst = configuration.ScanMode == VideoScanMode.Unspecified
                        || configuration.TopFieldFirst,
                };
                _formatStatus = VideoInputFormatStatus.FromReadback(configuration, _format);

                LogFormatReadback(configuration, width, height, fps);

                _capture = capture;
                _cts = new CancellationTokenSource();
                IsCapturing = true;

                _captureThread = new Thread(() => CaptureLoop(capture, _cts.Token))
                {
                    Name = "VideoCapture",
                    IsBackground = true,
                };
                _captureThread.Start();

                SetHealth(new CaptureHealth
                {
                    State = CaptureHealthState.Running,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                });

                _logger.LogInformation("Video capture started: {Device} {Width}x{Height} @ {Fps:F2} fps",
                    device.Name, width, height, fps);
            }
            catch (Exception ex)
            {
                capture?.Dispose();
                _capture = null;
                _format = null;
                IsCapturing = false;
                SetHealth(Health with
                {
                    State = CaptureHealthState.Faulted,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                    TotalCaptureFailures = Health.TotalCaptureFailures + 1,
                    ConsecutiveCaptureFailures = 1,
                    Error = ex.Message,
                });
                throw;
            }
        }, cancellationToken);
    }

    private static void ApplyRequestedFormat(VideoCapture capture, VideoInputConfiguration configuration)
    {
        if (configuration.RequestedWidth is { } width)
            capture.Set(VideoCaptureProperties.FrameWidth, width);
        if (configuration.RequestedHeight is { } height)
            capture.Set(VideoCaptureProperties.FrameHeight, height);
        if (configuration.RequestedFrameRate is { } frameRate)
            capture.Set(VideoCaptureProperties.Fps, frameRate);
    }

    private void LogFormatReadback(
        VideoInputConfiguration configuration,
        int actualWidth,
        int actualHeight,
        double actualFrameRate)
    {
        if (configuration.Standard == VideoInputStandard.Unspecified)
        {
            _logger.LogWarning(
                "Video input standard is unspecified; using driver format {Width}x{Height} @ {Fps:F3}",
                actualWidth, actualHeight, actualFrameRate);
        }
        else if (configuration.RequestedWidth != actualWidth
                 || configuration.RequestedHeight != actualHeight
                 || configuration.RequestedFrameRate is { } requestedFps
                    && Math.Abs(requestedFps - actualFrameRate) > 0.05)
        {
            _logger.LogWarning(
                "Capture driver did not honor requested {Standard} format. Requested {RequestedWidth}x{RequestedHeight} @ {RequestedFps:F3}; actual {ActualWidth}x{ActualHeight} @ {ActualFps:F3}",
                configuration.Standard,
                configuration.RequestedWidth,
                configuration.RequestedHeight,
                configuration.RequestedFrameRate,
                actualWidth,
                actualHeight,
                actualFrameRate);
        }

        if (configuration.ScanMode == VideoScanMode.Unspecified)
        {
            _logger.LogWarning(
                "Video scan mode is unspecified; legacy height-based interlace/TFF assumption is in effect");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing && _captureThread is null && _capture is null && _cts is null)
        {
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

        IsCapturing = false;
        _cts?.Cancel();
        return Task.Run(() =>
        {
            _captureThread?.Join(TimeSpan.FromSeconds(3));
            _captureThread = null;
            _cts?.Dispose();
            _cts = null;
            // Normally disposed by CaptureLoop on its owning thread. This fallback
            // covers a thread that could not be started or joined.
            try { _capture?.Release(); } catch (Exception) { }
            _capture?.Dispose();
            _capture = null;
            _format = null;
            CompleteSubscriptions();
            SetHealth(Health with
            {
                State = CaptureHealthState.Stopped,
                Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                ConsecutiveCaptureFailures = 0,
            });
            _logger.LogInformation("Video capture stopped");
        }, CancellationToken.None);
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

    private void CaptureLoop(VideoCapture capture, CancellationToken ct)
    {
        var format = _format!;
        using var mat = new Mat();
        long frameNumber = 0;
        long deliverySequence = 0;
        var consecutiveFailures = 0;
        Exception? terminalError = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool ok;
                try
                {
                    ok = capture.Read(mat);
                }
                catch (Exception ex)
                {
                    terminalError = ex;
                    _logger.LogError(ex, "Video capture read failed");
                    break;
                }

                if (!ok || mat.Empty())
                {
                    consecutiveFailures++;
                    NoteCaptureFailure(consecutiveFailures, "Video capture returned no frame.");
                    if (consecutiveFailures > 50)
                    {
                        terminalError = new IOException(
                            "Video capture delivered no frames more than 50 times in a row.");
                        _logger.LogError("Video capture delivered no frames 50 times in a row; stopping");
                        break;
                    }
                    Thread.Sleep(5);
                    continue;
                }
                var recovered = consecutiveFailures > 0;
                consecutiveFailures = 0;

                // Normalize to BGR24 (OpenCV default) and detect format changes.
                var formatChanged = mat.Width != format.Width || mat.Height != format.Height;
                if (formatChanged)
                {
                    format = format with { Width = mat.Width, Height = mat.Height };
                    _format = format;
                    _formatStatus = VideoInputFormatStatus.FromReadback(_inputConfiguration, format);
                }

                var data = new byte[mat.Width * mat.Height * 3];
                if (mat.Channels() == 3 && mat.IsContinuous())
                {
                    System.Runtime.InteropServices.Marshal.Copy(mat.Data, data, 0, data.Length);
                }
                else
                {
                    using var bgr = new Mat();
                    Cv2.CvtColor(mat, bgr, mat.Channels() == 4
                        ? ColorConversionCodes.BGRA2BGR
                        : ColorConversionCodes.GRAY2BGR);
                    System.Runtime.InteropServices.Marshal.Copy(bgr.Data, data, 0, data.Length);
                }

                var timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns();
                var frame = new VideoFrame
                {
                    Data = data,
                    Format = format,
                    Timestamp100ns = timestamp100ns,
                    DeliverySequence = deliverySequence++,
                    FrameNumber = frameNumber++,
                };

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

                NoteDelivery(timestamp100ns, recovered, formatChanged);
            }
        }
        finally
        {
            IsCapturing = false;
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
            }
            try { capture.Release(); } catch (Exception) { }
            capture.Dispose();
            _format = null;

            if (terminalError is not null && !ct.IsCancellationRequested)
            {
                SetHealth(Health with
                {
                    State = CaptureHealthState.Faulted,
                    Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
                    Error = terminalError.Message,
                });
                CompleteSubscriptions(terminalError);
            }
            else
            {
                CompleteSubscriptions();
            }
        }
    }

    private void RemoveSubscription(CaptureSubscription<VideoFrame> subscription)
    {
        lock (_gate) _subscribers.Remove(subscription);
    }

    private void OnSubscriptionOverflow(CaptureOverflow _)
    {
        SetHealth(Health with
        {
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
            SubscriberOverflows = Health.SubscriberOverflows + 1,
        });
    }

    private void CompleteSubscriptions(Exception? error = null)
    {
        CaptureSubscription<VideoFrame>[] subscribers;
        lock (_gate)
        {
            subscribers = _subscribers.ToArray();
            _subscribers.Clear();
        }
        foreach (var subscription in subscribers) subscription.Complete(error);
    }

    private void NoteCaptureFailure(int consecutiveFailures, string error)
    {
        var health = Health;
        SetHealth(health with
        {
            State = CaptureHealthState.Degraded,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
            TotalCaptureFailures = health.TotalCaptureFailures + 1,
            ConsecutiveCaptureFailures = consecutiveFailures,
            Error = error,
        }, notify: consecutiveFailures == 1);
    }

    private void NoteDelivery(long timestamp100ns, bool recovered, bool discontinuity)
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
        if (recovered || discontinuity) RaiseHealthChanged(updated);
    }

    private void SetHealth(CaptureHealth health, bool notify = true)
    {
        lock (_healthGate) _health = health;
        if (notify) RaiseHealthChanged(health);
    }

    private void RaiseHealthChanged(CaptureHealth health)
    {
        try { HealthChanged?.Invoke(this, new CaptureHealthEventArgs(health)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Video capture health observer failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        CompleteSubscriptions();
    }
}
