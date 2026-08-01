using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Abstractions;

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Stopping,
    Faulted,
}

/// <summary>Progress/health information published while recording.</summary>
public sealed record RecordingStatus
{
    public RecordingState State { get; init; } = RecordingState.Idle;
    public TimeSpan Elapsed { get; init; }
    public long VideoFramesWritten { get; init; }
    public long VideoFramesDropped { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Synchronized A/V recording. Implemented over FFmpeg (external process) in later
/// phases; a no-op/simulated recorder is used until then.
/// </summary>
public interface IRecordingService : IAsyncDisposable
{
    RecordingStatus Status { get; }

    event EventHandler<RecordingStatus>? StatusChanged;

    /// <summary>
    /// Starts recording the given capture streams to <paramref name="outputPath"/>
    /// using <paramref name="profile"/>.
    /// </summary>
    Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        CancellationToken cancellationToken = default);

    /// <summary>Stops recording and finalizes the output file.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}