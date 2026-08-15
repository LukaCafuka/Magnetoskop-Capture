namespace Magnetoskop.Core.Models;

public enum CaptureHealthState
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Faulted,
}

/// <summary>Source and fan-out health snapshot for a video or audio capture service.</summary>
public sealed record CaptureHealth
{
    public CaptureHealthState State { get; init; } = CaptureHealthState.Stopped;
    public long Timestamp100ns { get; init; }
    public long? LastDeliveryTimestamp100ns { get; init; }
    public long ItemsDelivered { get; init; }
    public long TotalCaptureFailures { get; init; }
    public int ConsecutiveCaptureFailures { get; init; }
    public long SourceDiscontinuities { get; init; }
    public long SubscriberOverflows { get; init; }
    public string? Error { get; init; }
}

public sealed class CaptureHealthEventArgs : EventArgs
{
    public CaptureHealthEventArgs(CaptureHealth health) => Health = health;
    public CaptureHealth Health { get; }
}
