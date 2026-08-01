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
    private Task? _videoTask;
    private Task? _audioTask;
    private readonly Stopwatch _elapsed = new();
    private long _framesWritten;

    private RecordingStatus _status = new();

    public SimulatedRecordingService(ILogger<SimulatedRecordingService> logger)
    {
        _logger = logger;
    }

    public RecordingStatus Status => _status;

    public event EventHandler<RecordingStatus>? StatusChanged;

    public Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        CancellationToken cancellationToken = default)
    {
        if (_status.State is RecordingState.Recording or RecordingState.Starting)
        {
            throw new InvalidOperationException("Recording is already in progress.");
        }

        _logger.LogInformation(
            "Simulated recording started: profile={Profile}, output={Output}", profile.Id, outputPath);

        _framesWritten = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var videoReader = videoSource.Subscribe(capacity: 8);
        var audioReader = audioSource.Subscribe(capacity: 32);

        _elapsed.Restart();
        Publish(new RecordingStatus
        {
            State = RecordingState.Recording,
            OutputPath = outputPath,
        });

        _videoTask = Task.Run(async () =>
        {
            await foreach (var _ in videoReader.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _framesWritten);
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
    {
        if (_cts is null) return;

        Publish(_status with { State = RecordingState.Stopping });
        await _cts.CancelAsync();
        foreach (var task in new[] { _videoTask, _audioTask })
        {
            if (task is null) continue;
            try { await task; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _videoTask = null;
        _audioTask = null;
        _elapsed.Stop();

        Publish(new RecordingStatus
        {
            State = RecordingState.Idle,
            Elapsed = _elapsed.Elapsed,
            VideoFramesWritten = Interlocked.Read(ref _framesWritten),
        });
        _logger.LogInformation("Simulated recording stopped after {Elapsed} ({Frames} frames)",
            _elapsed.Elapsed, _framesWritten);
    }

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