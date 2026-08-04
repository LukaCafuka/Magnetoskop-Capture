using System.Threading.Channels;
using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Abstractions;

/// <summary>Video capture: device enumeration + a frame stream.</summary>
public interface IVideoCaptureService : IAsyncDisposable
{
    bool IsCapturing { get; }
    VideoFormat? CurrentFormat { get; }

    Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts capturing from the given device.</summary>
    Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a bounded subscription to the frame stream. Each subscriber gets its
    /// own channel; slow consumers drop the oldest frames instead of stalling capture.
    /// </summary>
    ChannelReader<VideoFrame> Subscribe(int capacity = 4);
}

/// <summary>Audio capture: device enumeration + a PCM stream + level metering.</summary>
public interface IAudioCaptureService : IAsyncDisposable
{
    bool IsCapturing { get; }
    AudioFormat? CurrentFormat { get; }

    /// <summary>
    /// Peak level per channel in the 0..1 range since the last read (consume-on-read).
    /// Implementations hold the max across capture buffers, return a copy, then zero.
    /// </summary>
    IReadOnlyList<float> PeakLevels { get; }

    Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Picks the best matching audio device for the given video device
    /// (used by auto-select when the user has not chosen audio explicitly).
    /// </summary>
    Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default);

    Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    ChannelReader<AudioBuffer> Subscribe(int capacity = 16);
}