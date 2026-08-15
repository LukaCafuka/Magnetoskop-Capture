using System.IO;
using System.Text.Json;
using Magnetoskop.App.Services;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.App.Tests;

public sealed class CaptureSessionTerminalArtifactTests
{
    private static readonly RecordingProfile Profile = RecordingProfile.CreateFfv1Archival();

    public static TheoryData<RecordingStopReason, RecordingOutcome, bool> TerminalResults => new()
    {
        { RecordingStopReason.QueueOverflow, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.FrameAuditOverflow, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.VideoStall, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.AudioStall, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.VideoDiscontinuity, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.AudioDiscontinuity, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.TimestampRegression, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.TimingTelemetryIncomplete, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.ExcessiveDrift, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.FormatChanged, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.CaptureEnded, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.LowDiskSpace, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.FinalizationTrimFailed, RecordingOutcome.Incomplete, true },
        { RecordingStopReason.EncoderExited, RecordingOutcome.Faulted, false },
        { RecordingStopReason.InternalFailure, RecordingOutcome.Faulted, false },
        { RecordingStopReason.Cancellation, RecordingOutcome.Cancelled, true },
    };

    [Theory]
    [MemberData(nameof(TerminalResults))]
    public async Task AutomaticTermination_AtomicallyFinalizesSidecar(
        RecordingStopReason reason,
        RecordingOutcome outcome,
        bool mediaFinalized)
    {
        var recorder = new ControlledRecordingService();
        var coordinator = CreateCoordinator(recorder);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-terminal").FullName;

        try
        {
            var outputPath = await coordinator.StartRecordingAsync(
                directory, Profile, "Test capture");
            var finalPath = Path.ChangeExtension(outputPath, ".json");
            var partialPath = finalPath + ".partial";

            Assert.True(File.Exists(partialPath));
            Assert.False(File.Exists(finalPath));

            var armedAt = new DateTimeOffset(2026, 8, 15, 12, 34, 56, TimeSpan.Zero);
            recorder.Complete(reason, outcome, mediaFinalized, armedAt);
            await WaitForFileAsync(finalPath);

            var metadata = await ReadMetadataAsync(finalPath);
            Assert.Equal(outcome.ToString(), metadata.Outcome);
            Assert.Equal(reason.ToString(), metadata.StopReason);
            Assert.Equal(mediaFinalized, metadata.MediaFinalized);
            Assert.Equal(armedAt, metadata.StartedAt);
            Assert.False(File.Exists(partialPath));
            Assert.True(File.Exists(Path.ChangeExtension(outputPath, ".frames.jsonl")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_FinalizesPlayableSessionWithShutdownReason()
    {
        var recorder = new ControlledRecordingService();
        var coordinator = CreateCoordinator(recorder);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-shutdown").FullName;

        try
        {
            var outputPath = await coordinator.StartRecordingAsync(
                directory, Profile, "Test capture");

            await coordinator.StopForShutdownAsync();

            var metadata = await ReadMetadataAsync(Path.ChangeExtension(outputPath, ".json"));
            Assert.Equal(RecordingOutcome.Completed.ToString(), metadata.Outcome);
            Assert.Equal(RecordingStopReason.ApplicationShutdown.ToString(), metadata.StopReason);
            Assert.True(metadata.MediaFinalized);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupFailure_RetainsTerminalEvidenceAndRemovesPartialSummary()
    {
        var recorder = new ControlledRecordingService { StartFailure = new IOException("encoder start failed") };
        var coordinator = CreateCoordinator(recorder);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-startup").FullName;

        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                coordinator.StartRecordingAsync(directory, Profile, "Test capture"));
            Assert.Equal("encoder start failed", error.Message);

            var finalPath = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var metadata = await ReadMetadataAsync(finalPath);
            Assert.Equal(RecordingOutcome.Faulted.ToString(), metadata.Outcome);
            Assert.Equal(RecordingStopReason.StartupFailure.ToString(), metadata.StopReason);
            Assert.False(metadata.MediaFinalized);
            Assert.Contains("encoder start failed", metadata.Error);
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedSessions_NeverOverwriteEarlierArtifacts()
    {
        var recorder = new ControlledRecordingService();
        var coordinator = CreateCoordinator(recorder);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-unique").FullName;

        try
        {
            var first = await coordinator.StartRecordingAsync(
                directory, Profile, "Test capture");
            await coordinator.StopRecordingAsync();
            var second = await coordinator.StartRecordingAsync(
                directory, Profile, "Test capture");
            await coordinator.StopRecordingAsync();

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(Path.ChangeExtension(first, ".json")));
            Assert.True(File.Exists(Path.ChangeExtension(second, ".json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommittedFrame_AuditUsesNearestPrecedingFreshLtcAndSafeInterpolation()
    {
        var recorder = new ControlledRecordingService();
        var vtr = new FakeVtrController { IsConnected = true };
        var receiptTimestamp = CaptureMonotonicClock.GetTimestamp100ns();
        var receiptWallClock = DateTimeOffset.UtcNow;
        vtr.CurrentStatus = new VtrStatus
        {
            Transport = TransportState.Playing,
            ServoLock = true,
            Timestamp = receiptWallClock,
            Timestamp100ns = receiptTimestamp,
        };
        vtr.CurrentTime = new TimeInformation
        {
            Ltc = new Timecode(1, 0, 0, 0),
            LtcSource = TimecodeSource.Ltc,
            LtcReceivedAt = receiptWallClock,
            LtcReceivedTimestamp100ns = receiptTimestamp,
            PrimarySource = TimecodeSource.Ltc,
        };
        var video = new FakeVideoCaptureService
        {
            IsCapturing = true,
            CurrentFormat = new VideoFormat
            {
                Width = 720,
                Height = 576,
                FrameRate = 25,
                Interlaced = true,
            },
        };
        var coordinator = new CaptureSessionCoordinator(
            vtr,
            video,
            new FakeAudioCaptureService { IsCapturing = true },
            recorder,
            NullLogger<CaptureSessionCoordinator>.Instance);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-audit").FullName;

        try
        {
            var outputPath = await coordinator.StartRecordingAsync(
                directory, Profile, "Test capture");
            recorder.EmitFrame(new VideoFrameCommitted
            {
                SequenceNumber = 0,
                SourceFrameNumber = 42,
                CaptureTimestamp = TimeSpan.FromTicks(
                    receiptTimestamp + TimeSpan.FromMilliseconds(400).Ticks),
                TimelineTimestamp = TimeSpan.Zero,
            });
            await coordinator.StopRecordingAsync();

            var auditPath = Path.ChangeExtension(outputPath, ".frames.jsonl");
            var line = Assert.Single(await File.ReadAllLinesAsync(auditPath));
            using var audit = JsonDocument.Parse(line);
            Assert.Equal(42, audit.RootElement.GetProperty("sourceFrameNumber").GetInt64());
            Assert.Equal("01:00:00:00", audit.RootElement.GetProperty("observedTimecode").GetString());
            Assert.Equal("01:00:00:10", audit.RootElement.GetProperty("estimatedTimecode").GetString());
            Assert.True(audit.RootElement.GetProperty("isEstimated").GetBoolean());
            Assert.Equal("Fresh", audit.RootElement.GetProperty("timecodeFreshness").GetString());

            var summary = await ReadMetadataAsync(Path.ChangeExtension(outputPath, ".json"));
            Assert.Equal("01:00:00:10", summary.FirstCommittedTimecode);
            Assert.Equal("01:00:00:10", summary.LastCommittedTimecode);
            Assert.Equal(1, summary.FrameAuditRecordsAccepted);
            Assert.Equal(1, summary.FrameAuditRecordsWritten);
            Assert.Equal(0, summary.FrameAuditRecordsRejected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedAuditRecord_DowngradesRacingCompletedResultToIncomplete()
    {
        var recorder = new ControlledRecordingService
        {
            HoldFrameAuditStopUntilCompletion = true,
        };
        var coordinator = CreateCoordinator(recorder);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-audit-overflow").FullName;

        try
        {
            var outputPath = await coordinator.StartRecordingAsync(
                directory, Profile, "Test capture");
            var timestamp = CaptureMonotonicClock.GetTimestamp100ns();

            // The producer intentionally outruns the bounded JSONL writer. Holding
            // the automatic overflow stop lets a normal terminal result win the
            // completion race, so finalization itself must preserve the audit loss.
            for (var sequence = 0; sequence < 10_000; sequence++)
            {
                recorder.EmitFrame(new VideoFrameCommitted
                {
                    SequenceNumber = sequence,
                    SourceFrameNumber = sequence,
                    CaptureTimestamp = TimeSpan.FromTicks(timestamp + sequence),
                    TimelineTimestamp = TimeSpan.FromTicks(sequence),
                });
            }

            recorder.Complete(
                RecordingStopReason.UserRequested,
                RecordingOutcome.Completed,
                mediaFinalized: true,
                DateTimeOffset.UtcNow);
            var finalPath = Path.ChangeExtension(outputPath, ".json");
            await WaitForFileAsync(finalPath);
            await coordinator.StopForShutdownAsync();

            var metadata = await ReadMetadataAsync(finalPath);
            Assert.True(metadata.FrameAuditRecordsRejected > 0);
            Assert.Equal(RecordingOutcome.Incomplete.ToString(), metadata.Outcome);
            Assert.Equal(
                RecordingStopReason.FrameAuditOverflow.ToString(),
                metadata.StopReason);
            Assert.Contains("rejected", metadata.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CaptureSessionCoordinator CreateCoordinator(ControlledRecordingService recorder)
        => new(
            new FakeVtrController(),
            new FakeVideoCaptureService { IsCapturing = true },
            new FakeAudioCaptureService { IsCapturing = true },
            recorder,
            NullLogger<CaptureSessionCoordinator>.Instance);

    private static async Task<RecordingMetadata> ReadMetadataAsync(string path)
        => JsonSerializer.Deserialize<RecordingMetadata>(await File.ReadAllTextAsync(path))
           ?? throw new InvalidOperationException("Sidecar JSON was empty.");

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ControlledRecordingService : IRecordingService
    {
        private TaskCompletionSource<RecordingResult> _completion = NewCompletion();
        private DateTimeOffset _startedAt;
        private string? _outputPath;
        private RecordingStartOptions _options = RecordingStartOptions.Default;
        private EventHandler<VideoFrameCommitted>? _videoFrameCommitted;

        public Exception? StartFailure { get; init; }
        public bool HoldFrameAuditStopUntilCompletion { get; init; }
        public RecordingStatus Status { get; private set; } = new();
        public Task<RecordingResult> Completion => _completion.Task;

        public event EventHandler<RecordingStatus>? StatusChanged;
        public event EventHandler<VideoFrameCommitted>? VideoFrameCommitted
        {
            add => _videoFrameCommitted += value;
            remove => _videoFrameCommitted -= value;
        }

        public void EmitFrame(VideoFrameCommitted frame)
            => _videoFrameCommitted?.Invoke(this, frame);

        public Task StartAsync(
            RecordingProfile profile,
            string outputPath,
            IVideoCaptureService videoSource,
            IAudioCaptureService audioSource,
            CancellationToken cancellationToken = default)
            => StartAsync(
                profile, outputPath, videoSource, audioSource,
                RecordingStartOptions.Default, cancellationToken);

        public Task StartAsync(
            RecordingProfile profile,
            string outputPath,
            IVideoCaptureService videoSource,
            IAudioCaptureService audioSource,
            RecordingStartOptions options,
            CancellationToken cancellationToken = default)
        {
            if (StartFailure is not null) throw StartFailure;
            _completion = NewCompletion();
            _startedAt = DateTimeOffset.UtcNow;
            _outputPath = outputPath;
            _options = options;
            Publish(new RecordingStatus
            {
                State = RecordingState.Recording,
                OutputPath = outputPath,
            });
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
            => _ = await StopAsync(RecordingStopReason.UserRequested, cancellationToken);

        public Task<RecordingResult> StopAsync(
            RecordingStopReason reason,
            CancellationToken cancellationToken = default)
        {
            if (reason == RecordingStopReason.FrameAuditOverflow
                && HoldFrameAuditStopUntilCompletion
                && !_completion.Task.IsCompleted)
            {
                return _completion.Task.WaitAsync(cancellationToken);
            }

            if (!_completion.Task.IsCompleted)
            {
                Complete(reason, OutcomeFor(reason), mediaFinalized: true, _startedAt);
            }
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(
            RecordingStopReason reason,
            RecordingOutcome outcome,
            bool mediaFinalized,
            DateTimeOffset startedAt)
        {
            var result = new RecordingResult
            {
                Outcome = outcome,
                StopReason = reason,
                OutputPath = _outputPath,
                StartedAt = startedAt,
                EndedAt = startedAt.AddMinutes(1),
                MediaFinalized = mediaFinalized,
                StartOptions = _options,
                Metrics = new RecordingMetrics
                {
                    VideoFramesReceived = 1500,
                    VideoFramesWritten = 1500,
                    AudioSamplesReceived = 2_880_000,
                    AudioSamplesWritten = 2_880_000,
                },
            };
            Publish(Status with
            {
                State = outcome == RecordingOutcome.Completed
                    ? RecordingState.Idle
                    : RecordingState.Faulted,
                StopReason = reason,
            });
            _completion.TrySetResult(result);
        }

        private void Publish(RecordingStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }

        private static RecordingOutcome OutcomeFor(RecordingStopReason reason) => reason switch
        {
            RecordingStopReason.None or RecordingStopReason.UserRequested
                or RecordingStopReason.ApplicationShutdown => RecordingOutcome.Completed,
            RecordingStopReason.Cancellation => RecordingOutcome.Cancelled,
            RecordingStopReason.StartupFailure or RecordingStopReason.EncoderExited
                or RecordingStopReason.InternalFailure => RecordingOutcome.Faulted,
            _ => RecordingOutcome.Incomplete,
        };

        private static TaskCompletionSource<RecordingResult> NewCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
