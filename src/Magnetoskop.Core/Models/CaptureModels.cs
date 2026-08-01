namespace Magnetoskop.Core.Models;

/// <summary>Identifies a video or audio capture device.</summary>
public sealed record CaptureDeviceInfo
{
    /// <summary>Stable identifier (device path / endpoint id / index string).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>Pixel formats used across the capture/recording pipeline.</summary>
public enum VideoPixelFormat
{
    Bgr24,
    Bgra32,
    Yuv422,
}

/// <summary>Describes the video stream format delivered by a capture source.</summary>
public sealed record VideoFormat
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double FrameRate { get; init; }
    public VideoPixelFormat PixelFormat { get; init; } = VideoPixelFormat.Bgr24;
    /// <summary>True when the source delivers interlaced material that must be preserved.</summary>
    public bool Interlaced { get; init; }
    /// <summary>True = top field first (only meaningful when <see cref="Interlaced"/>).</summary>
    public bool TopFieldFirst { get; init; } = true;

    public int BytesPerFrame => Width * Height * PixelFormat switch
    {
        VideoPixelFormat.Bgr24 => 3,
        VideoPixelFormat.Bgra32 => 4,
        VideoPixelFormat.Yuv422 => 2,
        _ => 3,
    };
}

/// <summary>A single captured video frame. The buffer is owned by the receiver after publication.</summary>
public sealed record VideoFrame
{
    public required byte[] Data { get; init; }
    public required VideoFormat Format { get; init; }
    /// <summary>Monotonic capture timestamp.</summary>
    public required TimeSpan Timestamp { get; init; }
    public long FrameNumber { get; init; }
}

/// <summary>Describes the PCM format delivered by an audio capture source.</summary>
public sealed record AudioFormat
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
}

/// <summary>A captured block of PCM audio.</summary>
public sealed record AudioBuffer
{
    public required byte[] Data { get; init; }
    public required int Length { get; init; }
    public required AudioFormat Format { get; init; }
    /// <summary>Monotonic capture timestamp of the first sample.</summary>
    public required TimeSpan Timestamp { get; init; }
}