using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Simulation;

/// <summary>
/// Generates SMPTE-style color bars with a moving element so the preview pipeline
/// can be exercised without a capture device.
/// </summary>
public sealed class SimulatedVideoCaptureService : IVideoCaptureService
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
    private readonly List<Channel<VideoFrame>> _subscribers = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimulatedVideoCaptureService(ILogger<SimulatedVideoCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public VideoFormat? CurrentFormat => IsCapturing ? PalFormat : null;

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
        _loop = Task.Run(() => GenerateAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Simulated video capture started ({Device})", device.Name);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing) return;

        IsCapturing = false;
        if (_cts is not null) await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _logger.LogInformation("Simulated video capture stopped");
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

    private async Task GenerateAsync(CancellationToken ct)
    {
        var format = PalFormat;
        long frameNumber = 0;
        var framePeriod = TimeSpan.FromSeconds(1.0 / format.FrameRate);
        var start = TimeSpan.FromMilliseconds(Environment.TickCount64);

        using var timer = new PeriodicTimer(framePeriod);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var frame = RenderFrame(format, frameNumber,
                TimeSpan.FromMilliseconds(Environment.TickCount64) - start);
            frameNumber++;

            lock (_gate)
            {
                foreach (var sub in _subscribers)
                {
                    sub.Writer.TryWrite(frame);
                }
            }
        }
    }

    private static VideoFrame RenderFrame(VideoFormat format, long frameNumber, TimeSpan timestamp)
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
            Timestamp = timestamp,
            FrameNumber = frameNumber,
        };
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