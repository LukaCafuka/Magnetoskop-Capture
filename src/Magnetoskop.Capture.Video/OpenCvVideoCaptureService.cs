using System.Threading.Channels;
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
public sealed class OpenCvVideoCaptureService : IVideoCaptureService
{
    private readonly ILogger<OpenCvVideoCaptureService> _logger;
    private readonly List<Channel<VideoFrame>> _subscribers = new();
    private readonly object _gate = new();

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private VideoFormat? _format;

    public OpenCvVideoCaptureService(ILogger<OpenCvVideoCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public VideoFormat? CurrentFormat => _format;

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CaptureDeviceInfo>>(() =>
        {
            try
            {
                return DirectShowDeviceEnumerator.Enumerate();
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
        if (!int.TryParse(device.Id, out var index))
        {
            throw new ArgumentException($"'{device.Id}' is not a valid capture device index.", nameof(device));
        }

        return Task.Run(() =>
        {
            var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                throw new IOException($"Cannot open video capture device '{device.Name}' (index {index}).");
            }

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
                // Analog PAL sources deliver interlaced fields woven into frames.
                // Whether the device preserves them must be validated per capture device.
                Interlaced = height >= 480,
                TopFieldFirst = true,
            };

            _capture = capture;
            _cts = new CancellationTokenSource();
            IsCapturing = true;

            _captureThread = new Thread(() => CaptureLoop(_cts.Token))
            {
                Name = "VideoCapture",
                IsBackground = true,
            };
            _captureThread.Start();

            _logger.LogInformation("Video capture started: {Device} {Width}x{Height} @ {Fps:F2} fps",
                device.Name, width, height, fps);
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing) return Task.CompletedTask;

        IsCapturing = false;
        _cts?.Cancel();
        return Task.Run(() =>
        {
            _captureThread?.Join(TimeSpan.FromSeconds(3));
            _captureThread = null;
            _cts?.Dispose();
            _cts = null;
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            _format = null;
            _logger.LogInformation("Video capture stopped");
        }, cancellationToken);
    }

    public ChannelReader<VideoFrame> Subscribe(int capacity = 4)
    {
        var channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
        });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }
        return channel.Reader;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var capture = _capture!;
        var format = _format!;
        using var mat = new Mat();
        long frameNumber = 0;
        var start = Environment.TickCount64;
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            bool ok;
            try
            {
                ok = capture.Read(mat);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video capture read failed");
                break;
            }

            if (!ok || mat.Empty())
            {
                if (++consecutiveFailures > 50)
                {
                    _logger.LogError("Video capture delivered no frames 50 times in a row; stopping");
                    break;
                }
                Thread.Sleep(5);
                continue;
            }
            consecutiveFailures = 0;

            // Normalize to BGR24 (OpenCV default) and detect format changes.
            if (mat.Width != format.Width || mat.Height != format.Height)
            {
                format = format with { Width = mat.Width, Height = mat.Height };
                _format = format;
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

            var frame = new VideoFrame
            {
                Data = data,
                Format = format,
                Timestamp = TimeSpan.FromMilliseconds(Environment.TickCount64 - start),
                FrameNumber = frameNumber++,
            };

            lock (_gate)
            {
                foreach (var sub in _subscribers)
                {
                    sub.Writer.TryWrite(frame);
                }
            }
        }

        IsCapturing = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_gate)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }
}