using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Abstractions;

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Stopping,
    Incomplete,
    Cancelled,
    Faulted,
}

/// <summary>The terminal outcome of one recording attempt.</summary>
public enum RecordingOutcome
{
    Completed,
    Incomplete,
    Faulted,
    Cancelled,
}

/// <summary>Why a recording session entered its terminal state.</summary>
public enum RecordingStopReason
{
    None,
    UserRequested,
    ApplicationShutdown,
    Cancellation,
    QueueOverflow,
    FrameAuditOverflow,
    VideoStall,
    AudioStall,
    VideoDiscontinuity,
    AudioDiscontinuity,
    TimestampRegression,
    TimingTelemetryIncomplete,
    ExcessiveDrift,
    FormatChanged,
    CaptureEnded,
    EncoderExited,
    LowDiskSpace,
    FinalizationTrimFailed,
    StartupFailure,
    InternalFailure,
}

/// <summary>Integrity and synchronization counters for one recording session.</summary>
public sealed record RecordingMetrics
{
    public long VideoFramesReceived { get; init; }
    public long VideoFramesWritten { get; init; }
    public long VideoFramesDropped { get; init; }
    public long VideoFramesTrimmedAtStart { get; init; }
    public long VideoFramesTrimmedAtEnd { get; init; }
    public long VideoSequenceGaps { get; init; }
    public long VideoFormatRejects { get; init; }
    public long AudioBuffersReceived { get; init; }
    public long AudioBuffersWritten { get; init; }
    public long AudioBuffersDropped { get; init; }
    public long AudioDiscontinuities { get; init; }
    public long AudioTimestampEstimatedBuffers { get; init; }
    public long AudioSamplesMissing { get; init; }
    public long AudioSamplesReceived { get; init; }
    /// <summary>
    /// Application-side sample budget expected to remain after final remuxing.
    /// This is not a decoded-container sample count; use ffprobe for media verification.
    /// </summary>
    public long AudioSamplesWritten { get; init; }
    /// <summary>
    /// Samples submitted to FFmpeg, including lookahead bounded by the strict video queue.
    /// </summary>
    public long AudioSamplesSubmittedToEncoder { get; init; }
    public long AudioSamplesInserted { get; init; }
    public long AudioSamplesTrimmed { get; init; }
    public long AudioSamplesTrimmedAtStart { get; init; }
    public long AudioSamplesTrimmedAtEnd { get; init; }
    /// <summary>Bounded audio lookahead submitted while FFmpeg interleaved blocking inputs.</summary>
    public long AudioSamplesInterleaveLookaheadSubmitted { get; init; }
    /// <summary>Submitted lookahead removed by a successful finalized-file remux.</summary>
    public long AudioSamplesInterleaveLookaheadTrimmed { get; init; }
    public int VideoQueueDepth { get; init; }
    public int AudioQueueDepth { get; init; }
    public long VideoQueuePublishAttempts { get; init; }
    public long AudioQueuePublishAttempts { get; init; }
    public long VideoQueueAccepted { get; init; }
    public long AudioQueueAccepted { get; init; }
    public long VideoQueueConsumed { get; init; }
    public long AudioQueueConsumed { get; init; }
    public long VideoQueueRejectedNew { get; init; }
    public long AudioQueueRejectedNew { get; init; }
    public int VideoQueueHighWatermark { get; init; }
    public int AudioQueueHighWatermark { get; init; }
    public long VideoQueueOverflows { get; init; }
    public long AudioQueueOverflows { get; init; }
    public TimeSpan? InitialAvOffset { get; init; }
    public TimeSpan? CurrentAvOffset { get; init; }
    public double EstimatedDriftPpm { get; init; }
    public double RequiredCorrectionPpm { get; init; }
    public double AppliedCorrectionPpm { get; init; }
    public double PeakEstimatedDriftPpm { get; init; }
    public double PeakRequiredCorrectionPpm { get; init; }
    public double PeakAppliedCorrectionPpm { get; init; }
    public bool SyncTelemetryComplete { get; init; } = true;
    public bool SyncWarning { get; init; }
}

/// <summary>Per-session calibration and strict-recording options.</summary>
public sealed record RecordingStartOptions
{
    public static RecordingStartOptions Default { get; } = new();

    /// <summary>
    /// Signed correction added to audio capture timestamps before initial alignment.
    /// Positive values make the audio timeline later; negative values make it earlier.
    /// One unit is 100 ns (a <see cref="TimeSpan"/> tick).
    /// </summary>
    public long AudioOffset100ns { get; init; }
    public bool IsCalibrated { get; init; }
    public string? CalibrationKey { get; init; }
    public DateTimeOffset? CalibratedAt { get; init; }
}

/// <summary>Immutable terminal report returned for every recording attempt.</summary>
public sealed record RecordingResult
{
    public RecordingOutcome Outcome { get; init; }
    public RecordingStopReason StopReason { get; init; }
    public string? OutputPath { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public TimeSpan Elapsed => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;
    /// <summary>True when FFmpeg exited normally after receiving EOF and wrote its trailer.</summary>
    public bool MediaFinalized { get; init; }
    public RecordingMetrics Metrics { get; init; } = new();
    public RecordingStartOptions StartOptions { get; init; } = RecordingStartOptions.Default;
    public string? Error { get; init; }
}

/// <summary>Published after one complete raw frame has been accepted by FFmpeg stdin.</summary>
public sealed record VideoFrameCommitted
{
    public long SequenceNumber { get; init; }
    public long SourceFrameNumber { get; init; }
    public TimeSpan CaptureTimestamp { get; init; }
    public TimeSpan TimelineTimestamp { get; init; }
}

/// <summary>Progress/health information published while recording.</summary>
public sealed record RecordingStatus
{
    public RecordingState State { get; init; } = RecordingState.Idle;
    public TimeSpan Elapsed { get; init; }
    public long VideoFramesWritten { get; init; }
    public long VideoFramesDropped { get; init; }
    public long AudioBuffersWritten { get; init; }
    public long AudioBuffersDropped { get; init; }
    public int VideoQueueDepth { get; init; }
    public int AudioQueueDepth { get; init; }
    public int VideoQueueHighWatermark { get; init; }
    public int AudioQueueHighWatermark { get; init; }
    public TimeSpan? InitialAvOffset { get; init; }
    public TimeSpan? CurrentAvOffset { get; init; }
    public double EstimatedDriftPpm { get; init; }
    public double RequiredCorrectionPpm { get; init; }
    public double AppliedCorrectionPpm { get; init; }
    public bool SyncTelemetryComplete { get; init; } = true;
    public bool SyncWarning { get; init; }
    public RecordingStopReason StopReason { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
}

/// <summary>Synchronized A/V recording through an external FFmpeg process.</summary>
public interface IRecordingService : IAsyncDisposable
{
    RecordingStatus Status { get; }

    /// <summary>
    /// Completes for the active recording after FFmpeg exits and terminal metrics are frozen.
    /// A new task is installed by each start.
    /// </summary>
    Task<RecordingResult> Completion { get; }

    event EventHandler<RecordingStatus>? StatusChanged;

    event EventHandler<VideoFrameCommitted>? VideoFrameCommitted;

    Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        CancellationToken cancellationToken = default);

    /// <summary>Starts with a persisted device-pair timing calibration.</summary>
    Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        RecordingStartOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Stops recording for the normal user-requested reason.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops recording with an explicit reason and returns its terminal report.</summary>
    Task<RecordingResult> StopAsync(
        RecordingStopReason reason,
        CancellationToken cancellationToken = default);
}
