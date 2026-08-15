using System.Diagnostics;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Simulation;

/// <summary>
/// Placeholder recorder for Phase 1: consumes frames/audio from the capture services
/// (exercising the channel plumbing and back-pressure) and counts them, but writes no
/// file. Replaced by the FFmpeg recorder in Phase 6.
/// </summary>
public sealed class SimulatedRecordingService : IRecordingService
{
    private readonly ILogger<SimulatedRecordingService> _logger;
    private CancellationTokenSource? _cts;
    private CaptureSubscription<VideoFrame>? _videoSubscription;
    private CaptureSubscription<AudioBuffer>? _audioSubscription;
    private Task? _videoTask;
    private Task? _audioTask;
    private readonly Stopwatch _elapsed = new();
    private long _framesWritten;
    private DateTimeOffset _startedAt;
    private string? _outputPath;
    private RecordingStartOptions _startOptions = RecordingStartOptions.Default;
    private TaskCompletionSource<RecordingResult> _completion = CompletedIdleSource();

    private RecordingStatus _status = new();

    public SimulatedRecordingService(ILogger<SimulatedRecordingService> logger)
    {
        _logger = logger;
    }

    public RecordingStatus Status => _status;

    public Task<RecordingResult> Completion => _completion.Task;

    public event EventHandler<RecordingStatus>? StatusChanged;
    public event EventHandler<VideoFrameCommitted>? VideoFrameCommitted;

    public Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        CancellationToken cancellationToken = default)
        => StartAsync(profile, outputPath, videoSource, audioSource,
            RecordingStartOptions.Default, cancellationToken);

    public Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        RecordingStartOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_status.State is RecordingState.Recording or RecordingState.Starting)
        {
            throw new InvalidOperationException("Recording is already in progress.");
        }

        _logger.LogInformation(
            "Simulated recording started: profile={Profile}, output={Output}", profile.Id, outputPath);

        _framesWritten = 0;
        _startedAt = DateTimeOffset.UtcNow;
        _outputPath = outputPath;
        _startOptions = options ?? throw new ArgumentNullException(nameof(options));
        _completion = NewCompletionSource();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _videoSubscription = videoSource.Subscribe(CaptureSubscriptionOptions.Recording(8));
        _audioSubscription = audioSource.Subscribe(CaptureSubscriptionOptions.Recording(32));
        var videoReader = _videoSubscription;
        var audioReader = _audioSubscription;

        _elapsed.Restart();
        Publish(new RecordingStatus
        {
            State = RecordingState.Recording,
            OutputPath = outputPath,
        });

        _videoTask = Task.Run(async () =>
        {
            await foreach (var frame in videoReader.ReadAllAsync(ct))
            {
                var written = Interlocked.Increment(ref _framesWritten);
                VideoFrameCommitted?.Invoke(this, new VideoFrameCommitted
                {
                    SequenceNumber = written - 1,
                    SourceFrameNumber = frame.FrameNumber,
                    CaptureTimestamp = frame.Timestamp,
                    TimelineTimestamp = TimeSpan.FromSeconds((written - 1) / 25.0),
                });
                if (_framesWritten % 25 == 0)
                {
                    Publish(_status with
                    {
                        Elapsed = _elapsed.Elapsed,
                        VideoFramesWritten = Interlocked.Read(ref _framesWritten),
                    });
                }
            }
        }, CancellationToken.None);

        _audioTask = Task.Run(async () =>
        {
            await foreach (var _ in audioReader.ReadAllAsync(ct))
            {
                // Consume audio; counting handled via video cadence for the stub.
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
        => _ = await StopAsync(RecordingStopReason.UserRequested, cancellationToken);

    public async Task<RecordingResult> StopAsync(
        RecordingStopReason reason,
        CancellationToken cancellationToken = default)
    {
        if (_cts is null)
        {
            return _completion.Task.IsCompletedSuccessfully
                ? _completion.Task.Result
                : await Completion.WaitAsync(cancellationToken);
        }

        Publish(_status with { State = RecordingState.Stopping });
        await _cts.CancelAsync();
        foreach (var task in new[] { _videoTask, _audioTask })
        {
            if (task is null) continue;
            try { await task; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        if (_videoSubscription is not null) await _videoSubscription.DisposeAsync();
        if (_audioSubscription is not null) await _audioSubscription.DisposeAsync();
        _videoSubscription = null;
        _audioSubscription = null;
        _videoTask = null;
        _audioTask = null;
        _elapsed.Stop();

        Publish(new RecordingStatus
        {
            State = OutcomeFor(reason) switch
            {
                RecordingOutcome.Completed => RecordingState.Idle,
                RecordingOutcome.Incomplete => RecordingState.Incomplete,
                RecordingOutcome.Cancelled => RecordingState.Cancelled,
                _ => RecordingState.Faulted,
            },
            Elapsed = _elapsed.Elapsed,
            VideoFramesWritten = Interlocked.Read(ref _framesWritten),
        });
        _logger.LogInformation("Simulated recording stopped after {Elapsed} ({Frames} frames)",
            _elapsed.Elapsed, _framesWritten);

        var result = new RecordingResult
        {
            Outcome = OutcomeFor(reason),
            StopReason = reason,
            OutputPath = _outputPath,
            StartedAt = _startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            MediaFinalized = true,
            Metrics = new RecordingMetrics
            {
                VideoFramesReceived = Interlocked.Read(ref _framesWritten),
                VideoFramesWritten = Interlocked.Read(ref _framesWritten),
            },
            StartOptions = _startOptions,
        };
        _completion.TrySetResult(result);
        return result;
    }

    private static TaskCompletionSource<RecordingResult> NewCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<RecordingResult> CompletedIdleSource()
    {
        var source = NewCompletionSource();
        source.SetResult(new RecordingResult
        {
            Outcome = RecordingOutcome.Completed,
            StopReason = RecordingStopReason.None,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
        });
        return source;
    }

    private static RecordingOutcome OutcomeFor(RecordingStopReason reason) => reason switch
    {
        RecordingStopReason.Cancellation => RecordingOutcome.Cancelled,
        RecordingStopReason.None or RecordingStopReason.UserRequested or RecordingStopReason.ApplicationShutdown
            => RecordingOutcome.Completed,
        RecordingStopReason.StartupFailure or RecordingStopReason.EncoderExited or RecordingStopReason.InternalFailure
            => RecordingOutcome.Faulted,
        _ => RecordingOutcome.Incomplete,
    };

    private void Publish(RecordingStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
