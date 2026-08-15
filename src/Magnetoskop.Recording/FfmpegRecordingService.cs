using System.Diagnostics;
using System.IO.Pipes;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Recording;

/// <summary>
/// Loss-intolerant A/V recorder. Capture subscriptions reject rather than replace queued
/// recording data; any overflow, discontinuity, timestamp regression, format change, or
/// stalled stream requests one graceful FFmpeg EOF/finalization path and produces a
/// terminal <see cref="RecordingResult"/>.
/// </summary>
public sealed class FfmpegRecordingService : IRecordingService
{
    private readonly ILogger<FfmpegRecordingService> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stopGate = new();
    private readonly object _stderrGate = new();
    private readonly System.Text.StringBuilder _stderrTail = new();

    private Process? _process;
    private NamedPipeServerStream? _audioPipe;
    private CaptureSubscription<VideoFrame>? _videoSubscription;
    private CaptureSubscription<AudioBuffer>? _audioSubscription;
    private CancellationTokenSource? _pumpCts;
    private CancellationTokenRegistration _startCancellationRegistration;
    private Task? _videoTask;
    private Task? _audioTask;
    private Task? _stderrTask;
    private Task? _watchdogTask;
    private Task? _processExitTask;
    private Task? _videoOverflowTask;
    private Task? _audioOverflowTask;
    private Task? _finalizerTask;
    private TaskCompletionSource<bool> _stopSignal = NewSignal();
    private TaskCompletionSource<bool> _audioInputComplete = NewSignal();
    private TaskCompletionSource<RecordingResult> _completion = CompletedIdleResult();

    private RecordingStatus _status = new();
    private bool _sessionActive;
    private RecordingStopReason _stopReason;
    private RecordingOutcome _stopOutcome;
    private string? _stopError;
    private string? _outputPath;
    private DateTimeOffset _startedAt;
    private long _startedQpc100ns;
    private long _lastVideoReceiveQpc100ns;
    private long _lastVideoWriteQpc100ns;
    private long _lastAudioReceiveQpc100ns;
    private long _lastAudioWriteQpc100ns;
    private long _stopCutoffQpc100ns;

    private long _videoFramesReceived;
    private long _videoFramesWritten;
    private long _videoSequenceGaps;
    private long _videoFormatRejects;
    private long _videoFramesTrimmedAtStart;
    private long _videoFramesTrimmedAtEnd;
    private long _audioBuffersReceived;
    private long _audioBuffersWritten;
    private long _audioDiscontinuities;
    private long _audioTimestampEstimatedBuffers;
    private long _audioSamplesReceived;
    private long _audioSamplesWritten;
    private long _audioSamplesInserted;
    private long _audioSamplesTrimmedAtStart;
    private long _audioSamplesTrimmedAtEnd;
    private long _audioSamplesInterleaveLookaheadSubmitted;
    private long _audioSamplesInterleaveLookaheadTrimmed;
    private long _audioSamplesMissing;
    private double _peakEstimatedDriftPpm;
    private double _peakRequiredCorrectionPpm;
    private double _peakAppliedCorrectionPpm;
    private TimeSpan? _measuredInitialAvOffset;
    private bool _syncWarning;
    private long _residualOutOfBoundsSince100ns;
    private TimeSpan _residualFaultThreshold;
    private double _videoFrameRate;
    private bool _audioEnabled;
    private long _latestAudioCoverage100ns;

    private AvDriftEstimator? _driftEstimator;
    private AdaptivePcmResampler? _audioResampler;
    private AvDriftSnapshot _driftSnapshot = new();
    private RecordingStartOptions _startOptions = RecordingStartOptions.Default;

    public FfmpegRecordingService(ILogger<FfmpegRecordingService> logger)
    {
        _logger = logger;
    }

    public RecordingStatus Status => _status;
    public Task<RecordingResult> Completion => _completion.Task;

    public event EventHandler<RecordingStatus>? StatusChanged;
    public event EventHandler<VideoFrameCommitted>? VideoFrameCommitted;

    /// <summary>Fault when either enabled capture stream or FFmpeg write makes no progress.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Stop and finalize before the destination drive is full.</summary>
    public long MinimumFreeDiskBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>Free-space reserve required in addition to the finalized source size for EOF remuxing.</summary>
    public long FinalizationFreeSpaceMarginBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Target duration of each strict recording queue.</summary>
    public TimeSpan StrictQueueDuration { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan DriftWarmup { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DriftWindow { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DriftUpdateInterval { get; set; } = TimeSpan.FromSeconds(1);
    public double MaximumCorrectionPpm { get; set; } = 1000;
    public double CorrectionSlewPpmPerSecond { get; set; } = 50;
    public TimeSpan SyncWarningThreshold { get; set; } = TimeSpan.FromMilliseconds(10);
    public TimeSpan SyncResidualFaultDuration { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PhaseCorrectionTimeConstant { get; set; } = TimeSpan.FromSeconds(30);

    public async Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        CancellationToken cancellationToken = default)
        => await StartAsync(profile, outputPath, videoSource, audioSource,
            RecordingStartOptions.Default, cancellationToken);

    public async Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        RecordingStartOptions options,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionActive || _status.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Stopping)
                throw new InvalidOperationException("A recording is already in progress.");

            var videoFormat = videoSource.CurrentFormat
                ?? throw new InvalidOperationException("Video capture must be running before recording starts.");
            var audioFormat = audioSource.IsCapturing ? audioSource.CurrentFormat : null;
            var ffmpegPath = FfmpegLocator.Find()
                ?? throw new FileNotFoundException(
                    "ffmpeg.exe was not found. Place it next to the application, add it to PATH, or configure its location.");

            ArgumentNullException.ThrowIfNull(options);
            ResetSession(outputPath, videoFormat, audioFormat, options);
            Publish(new RecordingStatus { State = RecordingState.Starting, OutputPath = outputPath });

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

                // FFmpeg and its audio endpoint must be ready before loss-intolerant
                // capture subscriptions are armed. Otherwise process startup latency
                // is incorrectly charged to the recording queues.
                string? pipePath = null;
                if (audioFormat is not null)
                {
                    var pipeName = $"magnetoskop_audio_{Guid.NewGuid():N}";
                    pipePath = $@"\\.\pipe\{pipeName}";
                    _audioPipe = new NamedPipeServerStream(
                        pipeName, PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        inBufferSize: 0, outBufferSize: 1 << 20);
                }

                var args = FfmpegArgumentsBuilder.Build(
                    profile, videoFormat, audioFormat, pipePath, TimeSpan.Zero, outputPath);
                var process = CreateProcess(ffmpegPath, FfmpegArgumentsBuilder.Join(args));
                if (!process.Start()) throw new IOException("Failed to start the ffmpeg process.");
                _process = process;
                _stderrTask = Task.Run(() => DrainStderrAsync(process), CancellationToken.None);

                if (_audioPipe is not null)
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await _audioPipe.WaitForConnectionAsync(connectTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new IOException("ffmpeg did not connect to the audio pipe within 10 s. " + FfmpegErrorTail());
                    }
                }

                var videoCapacity = Math.Max(4,
                    (int)Math.Ceiling(videoFormat.FrameRate * StrictQueueDuration.TotalSeconds));
                // WASAPI normally publishes about 10-20 ms at a time. A 100 Hz bound
                // preserves the requested queue duration without blocking its callback.
                var audioCapacity = Math.Max(16,
                    (int)Math.Ceiling(100 * StrictQueueDuration.TotalSeconds));

                ArmSessionClock();
                _videoSubscription = videoSource.Subscribe(CaptureSubscriptionOptions.Recording(videoCapacity));
                if (audioFormat is not null)
                    _audioSubscription = audioSource.Subscribe(CaptureSubscriptionOptions.Recording(audioCapacity));

                // Align to the first media point shared by both streams. Raw FFmpeg
                // inputs then both begin at timestamp zero: early video frames and early
                // PCM samples are explicitly counted trims; silence is never synthesized.
                using var firstMediaTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                firstMediaTimeout.CancelAfter(StallTimeout);
                var firstVideoTask = _videoSubscription.ReadAsync(firstMediaTimeout.Token).AsTask();
                Task<AudioBuffer?> firstAudioTask = _audioSubscription is null
                    ? Task.FromResult<AudioBuffer?>(null)
                    : ReadFirstAudioAsync(_audioSubscription, firstMediaTimeout.Token);
                try
                {
                    await Task.WhenAll(firstVideoTask, firstAudioTask);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Timed out after {StallTimeout.TotalSeconds:F0} s waiting for the first live A/V timestamps.");
                }
                var firstVideo = await firstVideoTask;
                var firstAudio = await firstAudioTask;
                var originalFirstVideoTimestamp100ns = firstVideo.Timestamp100ns;
                long initialAudioTrimSamples = 0;
                if (firstAudio is not null && audioFormat is not null)
                {
                    var calibratedAudioStart100ns = firstAudio.Timestamp100ns + options.AudioOffset100ns;
                    _measuredInitialAvOffset = TimeSpan.FromTicks(
                        calibratedAudioStart100ns - originalFirstVideoTimestamp100ns);

                    while (firstVideo.Timestamp100ns < calibratedAudioStart100ns)
                    {
                        Interlocked.Increment(ref _videoFramesTrimmedAtStart);
                        try
                        {
                            firstVideo = await _videoSubscription.ReadAsync(firstMediaTimeout.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"Timed out after {StallTimeout.TotalSeconds:F0} s aligning the first video frame to audio.");
                        }
                    }

                    initialAudioTrimSamples = Math.Max(0, (long)Math.Ceiling(
                        (firstVideo.Timestamp100ns - calibratedAudioStart100ns)
                        * audioFormat.SampleRate / 10_000_000.0));
                }
                var overflowDuringAlignment = _videoSubscription.Metrics.RejectedNew > 0
                    || _audioSubscription?.Metrics.RejectedNew > 0;

                var ct = _pumpCts!.Token;
                _videoTask = Task.Run(
                    () => PumpVideoAsync(_videoSubscription, process, videoFormat, firstVideo, ct), CancellationToken.None);
                if (_audioSubscription is not null && _audioPipe is not null && audioFormat is not null)
                {
                    _audioTask = Task.Run(
                        () => PumpAudioAsync(
                            _audioSubscription, _audioPipe, audioFormat, videoFormat.FrameRate,
                            initialAudioTrimSamples, firstAudio!, ct), CancellationToken.None);
                }

                _videoOverflowTask = Task.Run(
                    () => WatchOverflowAsync(_videoSubscription, "video", ct), CancellationToken.None);
                if (_audioSubscription is not null)
                {
                    _audioOverflowTask = Task.Run(
                        () => WatchOverflowAsync(_audioSubscription, "audio", ct), CancellationToken.None);
                }

                _watchdogTask = Task.Run(() => WatchdogAsync(outputPath, audioFormat is not null, ct), CancellationToken.None);
                _processExitTask = Task.Run(() => MonitorProcessExitAsync(process, ct), CancellationToken.None);
                _finalizerTask = Task.Run(() => FinalizeWhenRequestedAsync(process), CancellationToken.None);
                _startCancellationRegistration = cancellationToken.Register(
                    () => RequestStop(RecordingStopReason.Cancellation, "Recording start token was cancelled."));

                Publish(StatusFromMetrics(RecordingState.Recording));
                if (overflowDuringAlignment)
                {
                    RequestStop(RecordingStopReason.QueueOverflow,
                        "A strict capture queue overflowed during initial A/V alignment; " +
                        "the accepted contiguous prefix is being finalized.");
                }
                _logger.LogInformation("Recording started → {Output} ({Profile})", outputPath, profile.DisplayName);
            }
            catch (Exception ex)
            {
                await FailStartupAsync(ex);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ResetSession(
        string outputPath,
        VideoFormat videoFormat,
        AudioFormat? audioFormat,
        RecordingStartOptions options)
    {
        _outputPath = outputPath;
        _startOptions = options;
        _startedAt = default;
        _startedQpc100ns = 0;
        _lastVideoReceiveQpc100ns = 0;
        _lastVideoWriteQpc100ns = 0;
        _lastAudioReceiveQpc100ns = 0;
        _lastAudioWriteQpc100ns = 0;
        _stopCutoffQpc100ns = 0;
        _sessionActive = true;
        _stopReason = RecordingStopReason.None;
        _stopOutcome = RecordingOutcome.Completed;
        _stopError = null;
        _stopSignal = NewSignal();
        _audioInputComplete = NewSignal();
        _completion = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pumpCts = new CancellationTokenSource();
        _stderrTail.Clear();

        _videoFramesReceived = 0;
        _videoFramesWritten = 0;
        _videoSequenceGaps = 0;
        _videoFormatRejects = 0;
        _videoFramesTrimmedAtStart = 0;
        _videoFramesTrimmedAtEnd = 0;
        _audioBuffersReceived = 0;
        _audioBuffersWritten = 0;
        _audioDiscontinuities = 0;
        _audioTimestampEstimatedBuffers = 0;
        _audioSamplesReceived = 0;
        _audioSamplesWritten = 0;
        _audioSamplesInserted = 0;
        _audioSamplesTrimmedAtStart = 0;
        _audioSamplesTrimmedAtEnd = 0;
        _audioSamplesInterleaveLookaheadSubmitted = 0;
        _audioSamplesInterleaveLookaheadTrimmed = 0;
        _audioSamplesMissing = 0;
        _peakEstimatedDriftPpm = 0;
        _peakRequiredCorrectionPpm = 0;
        _peakAppliedCorrectionPpm = 0;
        _measuredInitialAvOffset = null;
        _syncWarning = false;
        _residualOutOfBoundsSince100ns = 0;
        _residualFaultThreshold = TimeSpan.FromSeconds(
            1.0 / (videoFormat.FrameRate * (videoFormat.Interlaced ? 2.0 : 1.0)));
        _videoFrameRate = videoFormat.FrameRate;
        _audioEnabled = audioFormat is not null;
        _latestAudioCoverage100ns = 0;

        _driftSnapshot = new AvDriftSnapshot();
        _driftEstimator = audioFormat is null ? null : new AvDriftEstimator(
            videoFormat.FrameRate, audioFormat.SampleRate,
            DriftWarmup, DriftWindow, DriftUpdateInterval,
            MaximumCorrectionPpm, CorrectionSlewPpmPerSecond,
            PhaseCorrectionTimeConstant);
        _audioResampler = audioFormat is null ? null : new AdaptivePcmResampler(
            audioFormat.SampleRate, audioFormat.Channels, audioFormat.BitsPerSample,
            MaximumCorrectionPpm, CorrectionSlewPpmPerSecond);
    }

    private void ArmSessionClock()
    {
        _startedAt = DateTimeOffset.UtcNow;
        _startedQpc100ns = CaptureMonotonicClock.GetTimestamp100ns();
        _lastVideoReceiveQpc100ns = _startedQpc100ns;
        _lastVideoWriteQpc100ns = _startedQpc100ns;
        _lastAudioReceiveQpc100ns = _startedQpc100ns;
        _lastAudioWriteQpc100ns = _startedQpc100ns;
    }

    private static Process CreateProcess(string ffmpegPath, string argsLine) => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = argsLine,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        },
        EnableRaisingEvents = true,
    };

    private async Task PumpVideoAsync(
        CaptureSubscription<VideoFrame> reader,
        Process process,
        VideoFormat expectedFormat,
        VideoFrame firstFrame,
        CancellationToken ct)
    {
        var stdin = process.StandardInput.BaseStream;
        long? expectedSequence = null;
        long? lastTimestamp = null;
        try
        {
            if (!await WriteFrameAsync(firstFrame)) return;
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                if (!await WriteFrameAsync(frame)) break;
            }

            if (!ct.IsCancellationRequested && !IsStopRequested())
                RequestStop(RecordingStopReason.CaptureEnded, "Video capture stream ended during recording.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException ex)
        {
            RequestStop(RecordingStopReason.EncoderExited, $"Video pipe failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            RequestStop(RecordingStopReason.InternalFailure, $"Video pump failed: {ex.Message}");
        }
        finally
        {
            if (IsStopRequested())
            {
                while (reader.TryRead(out _))
                {
                    Interlocked.Increment(ref _videoFramesReceived);
                    Interlocked.Increment(ref _videoFramesTrimmedAtEnd);
                }
            }
            try { stdin.Close(); } catch (IOException) { }
        }

        async Task<bool> WriteFrameAsync(VideoFrame frame)
        {
            Interlocked.Increment(ref _videoFramesReceived);
            Interlocked.Exchange(ref _lastVideoReceiveQpc100ns, CaptureMonotonicClock.GetTimestamp100ns());

            var cutoff = Interlocked.Read(ref _stopCutoffQpc100ns);
            var frameDuration100ns = (long)Math.Round(10_000_000.0 / expectedFormat.FrameRate);
            if (cutoff != 0 && frame.Timestamp100ns + frameDuration100ns > cutoff)
            {
                Interlocked.Increment(ref _videoFramesTrimmedAtEnd);
                return false;
            }

            if (frame.Data.Length != expectedFormat.BytesPerFrame || frame.Format != expectedFormat)
            {
                Interlocked.Increment(ref _videoFormatRejects);
                RequestStop(RecordingStopReason.FormatChanged,
                    "Video format changed or a frame had an invalid byte count.");
                return false;
            }

            if (expectedSequence is { } expected && frame.DeliverySequence != expected)
            {
                Interlocked.Add(ref _videoSequenceGaps, Math.Max(1, frame.DeliverySequence - expected));
                RequestStop(RecordingStopReason.VideoDiscontinuity,
                    $"Video delivery sequence jumped from {expected - 1} to {frame.DeliverySequence}.");
                return false;
            }
            expectedSequence = frame.DeliverySequence + 1;

            if (lastTimestamp is { } prior && frame.Timestamp100ns <= prior)
            {
                RequestStop(RecordingStopReason.TimestampRegression,
                    $"Video timestamp regressed from {prior} to {frame.Timestamp100ns}.");
                return false;
            }
            lastTimestamp = frame.Timestamp100ns;

            if (_driftEstimator is not null)
                _driftSnapshot = _driftEstimator.ObserveVideo(frame.Timestamp100ns, frame.DeliverySequence);

            if (!await WaitForAudioCoverageAsync(frame.Timestamp100ns + frameDuration100ns))
            {
                Interlocked.Increment(ref _videoFramesTrimmedAtEnd);
                return false;
            }

            await stdin.WriteAsync(frame.Data, ct);
            var sequence = Interlocked.Increment(ref _videoFramesWritten) - 1;
            Interlocked.Exchange(ref _lastVideoWriteQpc100ns, CaptureMonotonicClock.GetTimestamp100ns());
            VideoFrameCommitted?.Invoke(this, new VideoFrameCommitted
            {
                SequenceNumber = sequence,
                SourceFrameNumber = frame.FrameNumber,
                CaptureTimestamp = frame.Timestamp,
                TimelineTimestamp = TimeSpan.FromSeconds(sequence / expectedFormat.FrameRate),
            });

            if ((sequence + 1) % Math.Max(1, (int)Math.Round(expectedFormat.FrameRate)) == 0)
                Publish(StatusFromMetrics(RecordingState.Recording));
            return true;
        }

        async Task<bool> WaitForAudioCoverageAsync(long frameEndTimestamp100ns)
        {
            if (!_audioEnabled) return true;
            while (Interlocked.Read(ref _latestAudioCoverage100ns) < frameEndTimestamp100ns)
            {
                if (_audioInputComplete.Task.IsCompleted) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(2), ct);
            }
            return true;
        }
    }

    private async Task PumpAudioAsync(
        CaptureSubscription<AudioBuffer> reader,
        NamedPipeServerStream pipe,
        AudioFormat expectedFormat,
        double videoFrameRate,
        long initialTrimSamples,
        AudioBuffer firstBuffer,
        CancellationToken ct)
    {
        long? expectedSampleIndex = null;
        long? lastTimestamp = null;
        var lastCorrectionUpdate = CaptureMonotonicClock.GetTimestamp100ns();
        var blockAlign = expectedFormat.Channels * (expectedFormat.BitsPerSample / 8);
        var pending = new PcmPendingBuffer(blockAlign);
        var maxPendingSamples = Math.Max(
            expectedFormat.SampleRate,
            (long)Math.Ceiling(expectedFormat.SampleRate * StrictQueueDuration.TotalSeconds));
        var trimRemainingSamples = initialTrimSamples;
        var pendingGate = new object();
        var inputComplete = 0;
        var writerTask = Task.Run(WritePendingAsync, CancellationToken.None);
        try
        {
            if (!AcceptBuffer(firstBuffer)) return;
            await foreach (var buffer in reader.ReadAllAsync(ct))
            {
                if (!AcceptBuffer(buffer)) break;
            }
            Volatile.Write(ref inputComplete, 1);
            _audioInputComplete.TrySetResult(true);

            // Video is the master. Once its sealed queue is fully drained, emit at most
            // the exact sample budget represented by committed frames and trim the tail.
            var flushed = _audioResampler!.Flush();
            if (flushed.Length > 0)
            {
                lock (pendingGate) pending.Enqueue(flushed);
            }
            await AwaitQuietly(_videoTask);
            await writerTask;
            long pendingTail;
            lock (pendingGate) pendingTail = pending.SampleFrames;
            if (pendingTail > 0)
                Interlocked.Add(ref _audioSamplesTrimmedAtEnd, pendingTail);

            var finalVideoSamples = (long)Math.Floor(
                Interlocked.Read(ref _videoFramesWritten)
                * expectedFormat.SampleRate / videoFrameRate);
            var submittedSamples = Interlocked.Read(ref _audioSamplesWritten);
            var muxLookahead = Math.Max(0, submittedSamples - finalVideoSamples);
            if (muxLookahead > 0)
                Interlocked.Add(ref _audioSamplesInterleaveLookaheadSubmitted, muxLookahead);
            var finalDeficit = Math.Max(
                0, finalVideoSamples - submittedSamples);
            if (finalDeficit > 0)
            {
                // Do not report a healthy/complete recording when audio coverage ends
                // before the committed video prefix. Even a sub-field deficit can make
                // a muxer omit a tail video packet, so the terminal result must expose it.
                Interlocked.Add(ref _audioSamplesMissing, finalDeficit);
                RequestStop(RecordingStopReason.AudioDiscontinuity,
                    $"Final audio ended {finalDeficit} sample frame(s) before the video master.");
            }

            if (!ct.IsCancellationRequested && !IsStopRequested())
                RequestStop(RecordingStopReason.CaptureEnded, "Audio capture stream ended during recording.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException ex)
        {
            RequestStop(RecordingStopReason.EncoderExited, $"Audio pipe failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            RequestStop(RecordingStopReason.InternalFailure, $"Audio pump failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref inputComplete, 1);
            _audioInputComplete.TrySetResult(true);
            if (IsStopRequested()) DrainTrimmedTail();
            await AwaitQuietly(writerTask);
            try { pipe.Close(); } catch (IOException) { }
        }

        bool AcceptBuffer(AudioBuffer buffer)
        {
            Interlocked.Increment(ref _audioBuffersReceived);
            Interlocked.Add(ref _audioSamplesReceived, buffer.SampleCount);
            Interlocked.Exchange(ref _lastAudioReceiveQpc100ns, CaptureMonotonicClock.GetTimestamp100ns());

            if (buffer.Format != expectedFormat
                || buffer.Length < 0
                || buffer.Length > buffer.Data.Length
                || buffer.Length != buffer.SampleCount * blockAlign)
            {
                Interlocked.Increment(ref _audioDiscontinuities);
                RequestStop(RecordingStopReason.FormatChanged,
                    "Audio format changed or a PCM buffer was not sample-aligned.");
                return false;
            }
            if (buffer.Discontinuity)
            {
                Interlocked.Increment(ref _audioDiscontinuities);
                RequestStop(RecordingStopReason.AudioDiscontinuity,
                    "The audio capture API reported a discontinuity.");
                return false;
            }
            if (expectedSampleIndex is { } expected && buffer.FirstSampleIndex != expected)
            {
                Interlocked.Increment(ref _audioDiscontinuities);
                Interlocked.Add(ref _audioSamplesMissing, Math.Max(0, buffer.FirstSampleIndex - expected));
                RequestStop(RecordingStopReason.AudioDiscontinuity,
                    $"Audio sample position jumped from {expected} to {buffer.FirstSampleIndex}.");
                return false;
            }
            expectedSampleIndex = buffer.FirstSampleIndex + buffer.SampleCount;

            if (lastTimestamp is { } prior && buffer.Timestamp100ns <= prior)
            {
                RequestStop(RecordingStopReason.TimestampRegression,
                    $"Audio timestamp regressed from {prior} to {buffer.Timestamp100ns}.");
                return false;
            }
            lastTimestamp = buffer.Timestamp100ns;
            if (buffer.TimestampEstimated)
                Interlocked.Increment(ref _audioTimestampEstimatedBuffers);

            var calibratedTimestamp100ns = buffer.Timestamp100ns + _startOptions.AudioOffset100ns;
            var sourceFramesToKeep = buffer.SampleCount;
            var reachedCutoff = false;
            var cutoff = Interlocked.Read(ref _stopCutoffQpc100ns);
            if (cutoff != 0)
            {
                var framesBeforeCutoff = cutoff <= buffer.Timestamp100ns
                    ? 0
                    : (long)Math.Floor(
                        (cutoff - buffer.Timestamp100ns) * expectedFormat.SampleRate / 10_000_000.0);
                sourceFramesToKeep = (int)Math.Clamp(framesBeforeCutoff, 0, buffer.SampleCount);
                var tail = buffer.SampleCount - sourceFramesToKeep;
                if (tail > 0)
                {
                    Interlocked.Add(ref _audioSamplesTrimmedAtEnd, tail);
                    reachedCutoff = true;
                }
            }

            var retainedCoverage100ns = calibratedTimestamp100ns + (long)Math.Floor(
                sourceFramesToKeep * 10_000_000.0 / expectedFormat.SampleRate);
            AtomicMax(ref _latestAudioCoverage100ns, retainedCoverage100ns);

            ReadOnlyMemory<byte> pcm = buffer.Data.AsMemory(
                0, checked((int)(sourceFramesToKeep * blockAlign)));
            var trimmedFromBuffer = 0;
            if (trimRemainingSamples > 0)
            {
                trimmedFromBuffer = (int)Math.Min(trimRemainingSamples, pcm.Length / blockAlign);
                pcm = pcm[(trimmedFromBuffer * blockAlign)..];
                trimRemainingSamples -= trimmedFromBuffer;
                Interlocked.Add(ref _audioSamplesTrimmedAtStart, trimmedFromBuffer);
            }
            if (!pcm.IsEmpty)
            {
                // The first estimator observation is the first PCM sample retained after
                // startup alignment, not the raw buffer head that was intentionally cut.
                var retainedTimestamp100ns = calibratedTimestamp100ns
                    + (long)Math.Round(trimmedFromBuffer * 10_000_000.0 / expectedFormat.SampleRate);
                var retainedSampleIndex = buffer.FirstSampleIndex + trimmedFromBuffer;
                _driftSnapshot = _driftEstimator!.ObserveAudio(
                    retainedTimestamp100ns, retainedSampleIndex);
                _peakEstimatedDriftPpm = Math.Max(
                    _peakEstimatedDriftPpm, Math.Abs(_driftSnapshot.EstimatedDriftPpm));
                _peakRequiredCorrectionPpm = Math.Max(
                    _peakRequiredCorrectionPpm, Math.Abs(_driftSnapshot.RequiredCorrectionPpm));
                if (_driftSnapshot.IsReady
                    && (Math.Abs(_driftSnapshot.EstimatedDriftPpm) > MaximumCorrectionPpm
                        || Math.Abs(_driftSnapshot.RequiredCorrectionPpm) > MaximumCorrectionPpm))
                {
                    RequestStop(RecordingStopReason.ExcessiveDrift,
                        $"Required A/V correction {_driftSnapshot.RequiredCorrectionPpm:F1} ppm " +
                        $"(rate {_driftSnapshot.EstimatedDriftPpm:F1} ppm) exceeds the " +
                        $"{MaximumCorrectionPpm:F0} ppm limit.");
                    return false;
                }

                var now = CaptureMonotonicClock.GetTimestamp100ns();
                _audioResampler!.SetTargetCorrection(
                    _driftSnapshot.RequiredCorrectionPpm,
                    TimeSpan.FromTicks(Math.Max(0, now - lastCorrectionUpdate)));
                _driftSnapshot = _driftEstimator.ReportAppliedCorrection(
                    _audioResampler.AppliedCorrectionPpm);
                _peakAppliedCorrectionPpm = Math.Max(
                    _peakAppliedCorrectionPpm, Math.Abs(_audioResampler.AppliedCorrectionPpm));
                lastCorrectionUpdate = now;
                if (!UpdateSyncHealth(now)) return false;

                var resampled = _audioResampler.Process(pcm.Span);
                if (resampled.Length > 0)
                {
                    lock (pendingGate) pending.Enqueue(resampled);
                }
            }
            Interlocked.Increment(ref _audioBuffersWritten);

            if (reachedCutoff) return false;
            long pendingSamples;
            lock (pendingGate) pendingSamples = pending.SampleFrames;
            if (pendingSamples > maxPendingSamples)
            {
                RequestStop(RecordingStopReason.QueueOverflow,
                    $"Timestamp-aligned PCM pending queue exceeded {StrictQueueDuration.TotalSeconds:F1} s.");
                return false;
            }
            return true;
        }

        void DrainTrimmedTail()
        {
            while (reader.TryRead(out var buffer))
            {
                Interlocked.Increment(ref _audioBuffersReceived);
                Interlocked.Add(ref _audioSamplesReceived, buffer.SampleCount);
                Interlocked.Add(ref _audioSamplesTrimmedAtEnd, buffer.SampleCount);
            }
        }

        async Task WritePendingAsync()
        {
            while (!ct.IsCancellationRequested)
            {
                var writtenVideoFrames = Interlocked.Read(ref _videoFramesWritten);
                var videoFrames = writtenVideoFrames;
                if (_videoTask?.IsCompleted != true
                    && _videoSubscription is { } videoSubscription)
                {
                    // FFmpeg opens/probes the audio input before it reads raw video stdin.
                    // Budget against the loss-intolerant subscription's accepted contiguous
                    // prefix while FFmpeg interleaves its blocking inputs. On EOF, the final
                    // budget switches to the exact committed count. A finite-file stream-copy
                    // remux removes any accepted-but-uncommitted tail; that tail is counted
                    // separately below. The lookahead is therefore bounded by the strict video
                    // queue, rather than by an individual audio packet.
                    var admittedPrefix = Math.Max(0,
                        videoSubscription.Metrics.Enqueued
                        - Interlocked.Read(ref _videoFramesTrimmedAtStart));
                    videoFrames = Math.Max(videoFrames, admittedPrefix);
                }

                var videoBudget = (long)Math.Floor(
                    videoFrames * expectedFormat.SampleRate / videoFrameRate);
                var allowance = videoBudget - Interlocked.Read(ref _audioSamplesWritten);
                ReadOnlyMemory<byte> bytes;
                lock (pendingGate)
                {
                    bytes = allowance > 0
                        ? pending.DequeueSamples(Math.Min(allowance, 4096))
                        : ReadOnlyMemory<byte>.Empty;
                }

                if (!bytes.IsEmpty)
                {
                    await pipe.WriteAsync(bytes, ct);
                    var written = bytes.Length / blockAlign;
                    Interlocked.Add(ref _audioSamplesWritten, written);
                    Interlocked.Exchange(ref _lastAudioWriteQpc100ns,
                        CaptureMonotonicClock.GetTimestamp100ns());
                    continue;
                }

                if (Volatile.Read(ref inputComplete) != 0 && _videoTask?.IsCompleted == true)
                    return;
                await Task.Delay(TimeSpan.FromMilliseconds(2), ct);
            }
        }
    }

    private async Task WatchOverflowAsync<T>(CaptureSubscription<T> subscription, string stream, CancellationToken ct)
    {
        try
        {
            var overflow = await subscription.FirstOverflow.WaitAsync(ct);
            RequestStop(RecordingStopReason.QueueOverflow,
                $"Strict {stream} queue overflowed ({overflow.Kind}, count {overflow.OverflowCount}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task WatchdogAsync(string outputPath, bool audioEnabled, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (_status.State != RecordingState.Recording) continue;

                var now = CaptureMonotonicClock.GetTimestamp100ns();
                if (now - Interlocked.Read(ref _lastVideoReceiveQpc100ns) > StallTimeout.Ticks
                    || now - Interlocked.Read(ref _lastVideoWriteQpc100ns) > StallTimeout.Ticks)
                {
                    RequestStop(RecordingStopReason.VideoStall,
                        $"No video capture/write progress for {StallTimeout.TotalSeconds:F0} s.");
                    return;
                }
                if (audioEnabled
                    && (now - Interlocked.Read(ref _lastAudioReceiveQpc100ns) > StallTimeout.Ticks
                        || now - Interlocked.Read(ref _lastAudioWriteQpc100ns) > StallTimeout.Ticks))
                {
                    RequestStop(RecordingStopReason.AudioStall,
                        $"No audio capture/write progress for {StallTimeout.TotalSeconds:F0} s.");
                    return;
                }

                if (GetFreeDiskBytes(outputPath) is { } free && free < MinimumFreeDiskBytes)
                {
                    RequestStop(RecordingStopReason.LowDiskSpace,
                        $"Recording stopped: free disk space fell below {MinimumFreeDiskBytes / (1024 * 1024)} MB.");
                    return;
                }

                Publish(StatusFromMetrics(RecordingState.Recording));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task MonitorProcessExitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
            lock (_stopGate)
            {
                if (!_sessionActive || _stopReason != RecordingStopReason.None) return;
            }
            RequestStop(RecordingStopReason.EncoderExited,
                $"ffmpeg exited unexpectedly (code {process.ExitCode}). {FfmpegErrorTail()}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void RequestStop(RecordingStopReason reason, string? error = null)
    {
        var outcome = OutcomeFor(reason);
        lock (_stopGate)
        {
            if (!_sessionActive) return;
            if (_stopReason == RecordingStopReason.None || IsMoreSevere(outcome, _stopOutcome))
            {
                _stopReason = reason;
                _stopOutcome = outcome;
                _stopError = error;
            }
            _stopCutoffQpc100ns = _stopCutoffQpc100ns == 0
                ? CaptureMonotonicClock.GetTimestamp100ns()
                : _stopCutoffQpc100ns;
            _stopSignal.TrySetResult(true);
        }
    }

    private bool UpdateSyncHealth(long now100ns)
    {
        if (!_driftSnapshot.IsReady || _driftSnapshot.CurrentOffset is not { } residual)
        {
            _syncWarning = false;
            _residualOutOfBoundsSince100ns = 0;
            return true;
        }

        var magnitude = residual.Duration();
        _syncWarning = magnitude > SyncWarningThreshold;
        if (magnitude <= _residualFaultThreshold)
        {
            _residualOutOfBoundsSince100ns = 0;
            return true;
        }

        if (_residualOutOfBoundsSince100ns == 0)
        {
            _residualOutOfBoundsSince100ns = now100ns;
            return true;
        }

        if (now100ns - _residualOutOfBoundsSince100ns < SyncResidualFaultDuration.Ticks)
            return true;

        RequestStop(RecordingStopReason.ExcessiveDrift,
            $"Corrected A/V residual remained {magnitude.TotalMilliseconds:F1} ms " +
            $"(>{_residualFaultThreshold.TotalMilliseconds:F1} ms) for " +
            $"{SyncResidualFaultDuration.TotalSeconds:F0} s.");
        return false;
    }

    private async Task FinalizeWhenRequestedAsync(Process process)
    {
        await _stopSignal.Task;
        Publish(StatusFromMetrics(RecordingState.Stopping));

        // Unregister the producers and complete their writers first. Buffered capture
        // data remains readable, so both encoder pumps can reach one common, finite
        // cutoff instead of losing accepted data through cancellation.
        _videoSubscription?.Dispose();
        _audioSubscription?.Dispose();

        var pumps = Task.WhenAll(
            _videoTask ?? Task.CompletedTask,
            _audioTask ?? Task.CompletedTask);
        try
        {
            await pumps.WaitAsync(StallTimeout);
        }
        catch (TimeoutException)
        {
            RequestStop(RecordingStopReason.EncoderExited,
                $"Encoder pumps did not drain within {StallTimeout.TotalSeconds:F0} s.");
            if (_pumpCts is not null) await _pumpCts.CancelAsync();
            await AwaitQuietly(pumps);
        }

        // Overflow/process/watchdog observers have no further work after both media
        // pumps have drained. Cancellation here cannot discard capture payloads.
        if (_pumpCts is not null) await _pumpCts.CancelAsync();

        var killed = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            killed = true;
            _logger.LogWarning("ffmpeg did not exit after EOF; killing");
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
        }

        await AwaitQuietly(_stderrTask);
        var exitCode = process.HasExited ? process.ExitCode : -1;
        var mediaFinalized = !killed && exitCode == 0;
        if (!mediaFinalized)
        {
            lock (_stopGate)
            {
                _stopOutcome = RecordingOutcome.Faulted;
                if (_stopReason is RecordingStopReason.None or RecordingStopReason.UserRequested)
                    _stopReason = RecordingStopReason.EncoderExited;
                _stopError ??= $"ffmpeg exited with code {exitCode}. {FfmpegErrorTail()}";
            }
        }
        else
        {
            var submittedLookahead = Interlocked.Read(
                ref _audioSamplesInterleaveLookaheadSubmitted);
            if (submittedLookahead > 0)
            {
                if (await TrimMuxLookaheadAsync(process.StartInfo.FileName))
                {
                    // These counters describe the delivered artifact, so commit them
                    // only after the same-directory replacement has succeeded.
                    Interlocked.Add(
                        ref _audioSamplesInterleaveLookaheadTrimmed, submittedLookahead);
                    Interlocked.Add(ref _audioSamplesTrimmedAtEnd, submittedLookahead);
                }
                else
                {
                    lock (_stopGate)
                    {
                        const string trimError =
                            "The file was finalized, but the bounded FFmpeg A/V interleave lookahead could not be trimmed.";
                        if (_stopOutcome == RecordingOutcome.Completed)
                        {
                            _stopOutcome = RecordingOutcome.Incomplete;
                            _stopReason = RecordingStopReason.FinalizationTrimFailed;
                        }
                        // A trim failure is secondary to an earlier queue/gap/stall
                        // integrity failure. Retain that causal stop reason and append
                        // the finalization diagnostic so neither problem is hidden.
                        _stopError = string.IsNullOrWhiteSpace(_stopError)
                            ? trimError
                            : $"{_stopError} {trimError}";
                    }
                }
            }

            if (Interlocked.Read(ref _audioTimestampEstimatedBuffers) > 0)
            {
                lock (_stopGate)
                {
                    if (_stopOutcome == RecordingOutcome.Completed)
                    {
                        _stopOutcome = RecordingOutcome.Incomplete;
                        _stopReason = RecordingStopReason.TimingTelemetryIncomplete;
                        _stopError ??= "One or more audio timestamps were estimated rather than device/QPC correlated.";
                    }
                }
            }
        }

        var result = BuildResult(mediaFinalized);
        await DisposeSessionResourcesAsync(process);
        lock (_stopGate) _sessionActive = false;

        Publish(StatusFromResult(result));
        _completion.TrySetResult(result);
        _logger.LogInformation(
            "Recording ended: {Outcome}/{Reason}, finalized={Finalized}, video={Frames}, audioSamples={Samples}",
            result.Outcome, result.StopReason, result.MediaFinalized,
            result.Metrics.VideoFramesWritten, result.Metrics.AudioSamplesWritten);
    }

    private async Task<bool> TrimMuxLookaheadAsync(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(_outputPath) || _videoFrameRate <= 0)
            return false;

        var durationSeconds = Interlocked.Read(ref _videoFramesWritten) / _videoFrameRate;
        if (durationSeconds <= 0) return false;

        var extension = Path.GetExtension(_outputPath);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(_outputPath))!,
            $"{Path.GetFileNameWithoutExtension(_outputPath)}.trim-{Guid.NewGuid():N}{extension}");
        try
        {
            var sourceLength = new FileInfo(_outputPath).Length;
            var margin = Math.Max(0, FinalizationFreeSpaceMarginBytes);
            var requiredFreeBytes = sourceLength > long.MaxValue - margin
                ? long.MaxValue
                : sourceLength + margin;
            if (GetFreeDiskBytes(temporaryPath) is { } freeBytes
                && freeBytes < requiredFreeBytes)
            {
                _logger.LogError(
                    "Skipping EOF remux: {FreeBytes} bytes free, but duplicating the {SourceBytes}-byte " +
                    "recording requires at least {RequiredBytes} bytes including reserve",
                    freeBytes, sourceLength, requiredFreeBytes);
                return false;
            }

            using var trim = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", _outputPath, "-map", "0", "-c", "copy",
                // Unlike limiting the live blocking pipes, this finite-file EOF remux
                // cannot deadlock input admission. The target is the exact committed
                // video budget; stream-copy packet granularity is verified by ffprobe.
                "-t", durationSeconds.ToString(
                    "0.#########", System.Globalization.CultureInfo.InvariantCulture),
                temporaryPath,
            })
            {
                trim.StartInfo.ArgumentList.Add(argument);
            }

            if (!trim.Start()) return false;
            var stderrTask = trim.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await trim.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { trim.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }

            var stderr = await stderrTask;
            if (trim.ExitCode != 0
                || !File.Exists(temporaryPath)
                || new FileInfo(temporaryPath).Length == 0)
            {
                _logger.LogError("FFmpeg lookahead trim failed ({ExitCode}): {Error}", trim.ExitCode, stderr);
                return false;
            }

            File.Move(temporaryPath, _outputPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not trim FFmpeg interleave lookahead");
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
        => _ = await StopAsync(RecordingStopReason.UserRequested, cancellationToken);

    public async Task<RecordingResult> StopAsync(
        RecordingStopReason reason,
        CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            if (!_sessionActive)
                return _completion.Task.IsCompletedSuccessfully
                    ? _completion.Task.Result
                    : IdleResult();
        }

        RequestStop(reason);
        return await _completion.Task.WaitAsync(cancellationToken);
    }

    private async Task FailStartupAsync(Exception exception)
    {
        lock (_stopGate)
        {
            _stopReason = exception is OperationCanceledException
                ? RecordingStopReason.Cancellation
                : RecordingStopReason.StartupFailure;
            _stopOutcome = exception is OperationCanceledException
                ? RecordingOutcome.Cancelled
                : RecordingOutcome.Faulted;
            _stopError = exception.Message;
        }

        if (_pumpCts is not null) await _pumpCts.CancelAsync();
        try { _process?.StandardInput.Close(); } catch (InvalidOperationException) { }
        try { _audioPipe?.Close(); } catch (IOException) { }
        if (_process is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
        }
        await AwaitQuietly(_stderrTask);

        var result = BuildResult(mediaFinalized: false);
        if (_process is { } failedProcess) await DisposeSessionResourcesAsync(failedProcess);
        else await DisposeSessionResourcesAsync(null);
        lock (_stopGate) _sessionActive = false;
        Publish(StatusFromResult(result));
        _completion.TrySetResult(result);
    }

    private RecordingResult BuildResult(bool mediaFinalized)
    {
        RecordingOutcome outcome;
        RecordingStopReason reason;
        string? error;
        lock (_stopGate)
        {
            outcome = _stopOutcome;
            reason = _stopReason;
            error = _stopError;
        }
        var endedAt = DateTimeOffset.UtcNow;
        var startedAt = _startedAt == default ? endedAt : _startedAt;
        return new RecordingResult
        {
            Outcome = outcome,
            StopReason = reason,
            OutputPath = _outputPath,
            StartedAt = startedAt,
            EndedAt = endedAt,
            MediaFinalized = mediaFinalized,
            Metrics = SnapshotMetrics(),
            StartOptions = _startOptions,
            Error = error,
        };
    }

    private RecordingMetrics SnapshotMetrics()
    {
        var videoQueue = _videoSubscription?.Metrics ?? default;
        var audioQueue = _audioSubscription?.Metrics ?? default;
        return new RecordingMetrics
        {
            VideoFramesReceived = Interlocked.Read(ref _videoFramesReceived),
            VideoFramesWritten = Interlocked.Read(ref _videoFramesWritten),
            VideoFramesDropped = videoQueue.RejectedNew + Interlocked.Read(ref _videoFormatRejects),
            VideoFramesTrimmedAtStart = Interlocked.Read(ref _videoFramesTrimmedAtStart),
            VideoFramesTrimmedAtEnd = Interlocked.Read(ref _videoFramesTrimmedAtEnd),
            VideoSequenceGaps = Interlocked.Read(ref _videoSequenceGaps),
            VideoFormatRejects = Interlocked.Read(ref _videoFormatRejects),
            AudioBuffersReceived = Interlocked.Read(ref _audioBuffersReceived),
            AudioBuffersWritten = Interlocked.Read(ref _audioBuffersWritten),
            AudioBuffersDropped = audioQueue.RejectedNew,
            AudioDiscontinuities = Interlocked.Read(ref _audioDiscontinuities),
            AudioTimestampEstimatedBuffers = Interlocked.Read(ref _audioTimestampEstimatedBuffers),
            AudioSamplesReceived = Interlocked.Read(ref _audioSamplesReceived),
            AudioSamplesSubmittedToEncoder = Interlocked.Read(ref _audioSamplesWritten),
            AudioSamplesWritten = Interlocked.Read(ref _audioSamplesWritten)
                - Interlocked.Read(ref _audioSamplesInterleaveLookaheadTrimmed),
            AudioSamplesInserted = Interlocked.Read(ref _audioSamplesInserted),
            AudioSamplesTrimmed = Interlocked.Read(ref _audioSamplesTrimmedAtStart)
                + Interlocked.Read(ref _audioSamplesTrimmedAtEnd),
            AudioSamplesTrimmedAtStart = Interlocked.Read(ref _audioSamplesTrimmedAtStart),
            AudioSamplesTrimmedAtEnd = Interlocked.Read(ref _audioSamplesTrimmedAtEnd),
            AudioSamplesInterleaveLookaheadSubmitted = Interlocked.Read(
                ref _audioSamplesInterleaveLookaheadSubmitted),
            AudioSamplesInterleaveLookaheadTrimmed = Interlocked.Read(
                ref _audioSamplesInterleaveLookaheadTrimmed),
            AudioSamplesMissing = Interlocked.Read(ref _audioSamplesMissing),
            VideoQueueDepth = videoQueue.QueueDepth,
            AudioQueueDepth = audioQueue.QueueDepth,
            VideoQueuePublishAttempts = videoQueue.PublishAttempts,
            AudioQueuePublishAttempts = audioQueue.PublishAttempts,
            VideoQueueAccepted = videoQueue.Enqueued,
            AudioQueueAccepted = audioQueue.Enqueued,
            VideoQueueConsumed = videoQueue.Dequeued,
            AudioQueueConsumed = audioQueue.Dequeued,
            VideoQueueRejectedNew = videoQueue.RejectedNew,
            AudioQueueRejectedNew = audioQueue.RejectedNew,
            VideoQueueHighWatermark = videoQueue.HighWatermark,
            AudioQueueHighWatermark = audioQueue.HighWatermark,
            VideoQueueOverflows = videoQueue.OverflowCount,
            AudioQueueOverflows = audioQueue.OverflowCount,
            InitialAvOffset = _measuredInitialAvOffset,
            CurrentAvOffset = _driftSnapshot.CurrentOffset,
            EstimatedDriftPpm = _driftSnapshot.EstimatedDriftPpm,
            RequiredCorrectionPpm = _driftSnapshot.RequiredCorrectionPpm,
            AppliedCorrectionPpm = _audioResampler?.AppliedCorrectionPpm ?? 0,
            PeakEstimatedDriftPpm = _peakEstimatedDriftPpm,
            PeakRequiredCorrectionPpm = _peakRequiredCorrectionPpm,
            PeakAppliedCorrectionPpm = _peakAppliedCorrectionPpm,
            SyncTelemetryComplete = Interlocked.Read(ref _audioTimestampEstimatedBuffers) == 0,
            SyncWarning = _syncWarning,
        };
    }

    private RecordingStatus StatusFromMetrics(RecordingState state)
    {
        var metrics = SnapshotMetrics();
        RecordingStopReason reason;
        string? error;
        lock (_stopGate)
        {
            reason = _stopReason;
            error = _stopError;
        }
        return new RecordingStatus
        {
            State = state,
            OutputPath = _outputPath,
            Elapsed = DateTimeOffset.UtcNow - _startedAt,
            VideoFramesWritten = metrics.VideoFramesWritten,
            VideoFramesDropped = metrics.VideoFramesDropped,
            AudioBuffersWritten = metrics.AudioBuffersWritten,
            AudioBuffersDropped = metrics.AudioBuffersDropped,
            VideoQueueDepth = metrics.VideoQueueDepth,
            AudioQueueDepth = metrics.AudioQueueDepth,
            VideoQueueHighWatermark = metrics.VideoQueueHighWatermark,
            AudioQueueHighWatermark = metrics.AudioQueueHighWatermark,
            InitialAvOffset = metrics.InitialAvOffset,
            CurrentAvOffset = metrics.CurrentAvOffset,
            EstimatedDriftPpm = metrics.EstimatedDriftPpm,
            RequiredCorrectionPpm = metrics.RequiredCorrectionPpm,
            AppliedCorrectionPpm = metrics.AppliedCorrectionPpm,
            SyncTelemetryComplete = metrics.SyncTelemetryComplete,
            SyncWarning = metrics.SyncWarning,
            StopReason = reason,
            Error = error,
        };
    }

    private RecordingStatus StatusFromResult(RecordingResult result) => new()
    {
        State = result.Outcome switch
        {
            RecordingOutcome.Completed => RecordingState.Idle,
            RecordingOutcome.Incomplete => RecordingState.Incomplete,
            RecordingOutcome.Cancelled => RecordingState.Cancelled,
            _ => RecordingState.Faulted,
        },
        OutputPath = result.OutputPath,
        Elapsed = result.Elapsed,
        VideoFramesWritten = result.Metrics.VideoFramesWritten,
        VideoFramesDropped = result.Metrics.VideoFramesDropped,
        AudioBuffersWritten = result.Metrics.AudioBuffersWritten,
        AudioBuffersDropped = result.Metrics.AudioBuffersDropped,
        VideoQueueHighWatermark = result.Metrics.VideoQueueHighWatermark,
        AudioQueueHighWatermark = result.Metrics.AudioQueueHighWatermark,
        InitialAvOffset = result.Metrics.InitialAvOffset,
        CurrentAvOffset = result.Metrics.CurrentAvOffset,
        EstimatedDriftPpm = result.Metrics.EstimatedDriftPpm,
        RequiredCorrectionPpm = result.Metrics.RequiredCorrectionPpm,
        AppliedCorrectionPpm = result.Metrics.AppliedCorrectionPpm,
        SyncTelemetryComplete = result.Metrics.SyncTelemetryComplete,
        SyncWarning = result.Metrics.SyncWarning,
        StopReason = result.StopReason,
        Error = result.Error,
    };

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                _logger.LogWarning("ffmpeg: {Line}", line);
                lock (_stderrGate)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 4000)
                        _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
            }
        }
        catch (InvalidOperationException) { }
    }

    private string FfmpegErrorTail()
    {
        lock (_stderrGate)
            return _stderrTail.Length > 0 ? $"ffmpeg output: {_stderrTail}" : string.Empty;
    }

    private async Task DisposeSessionResourcesAsync(Process? process)
    {
        _startCancellationRegistration.Dispose();
        if (_videoSubscription is not null) await _videoSubscription.DisposeAsync();
        if (_audioSubscription is not null) await _audioSubscription.DisposeAsync();
        _videoSubscription = null;
        _audioSubscription = null;
        _audioPipe?.Dispose();
        _audioPipe = null;
        process?.Dispose();
        _process = null;
        _pumpCts?.Dispose();
        _pumpCts = null;
        _audioResampler = null;
        _driftEstimator = null;
    }

    private void Publish(RecordingStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static int DrainChannel<T>(CaptureSubscription<T> reader)
    {
        var discarded = 0;
        while (reader.TryRead(out _)) discarded++;
        return discarded;
    }

    private static (int Buffers, long SampleFrames) DrainAudioChannel(
        CaptureSubscription<AudioBuffer> reader)
    {
        var buffers = 0;
        long sampleFrames = 0;
        while (reader.TryRead(out var buffer))
        {
            buffers++;
            sampleFrames += buffer.SampleCount;
        }
        return (buffers, sampleFrames);
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null) return;
        try { await task; } catch (Exception) { }
    }

    private static void AtomicMax(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static long? GetFreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
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

    private static bool IsMoreSevere(RecordingOutcome candidate, RecordingOutcome current)
        => Severity(candidate) > Severity(current);

    private static int Severity(RecordingOutcome outcome) => outcome switch
    {
        RecordingOutcome.Completed => 0,
        RecordingOutcome.Cancelled => 1,
        RecordingOutcome.Incomplete => 2,
        RecordingOutcome.Faulted => 3,
        _ => 0,
    };

    private bool IsStopRequested()
    {
        lock (_stopGate) return _stopReason != RecordingStopReason.None;
    }

    private static async Task<AudioBuffer?> ReadFirstAudioAsync(
        CaptureSubscription<AudioBuffer> subscription,
        CancellationToken cancellationToken)
        => await subscription.ReadAsync(cancellationToken);

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<RecordingResult> CompletedIdleResult()
    {
        var source = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(IdleResult());
        return source;
    }

    private static RecordingResult IdleResult() => new()
    {
        Outcome = RecordingOutcome.Completed,
        StopReason = RecordingStopReason.None,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        MediaFinalized = false,
    };

    /// <summary>
    /// Small aligned FIFO used between the adaptive resampler and the video-master
    /// sample budget. It deliberately exposes only complete interleaved sample frames.
    /// </summary>
    private sealed class PcmPendingBuffer
    {
        private readonly Queue<byte[]> _chunks = new();
        private readonly int _blockAlign;
        private int _headOffset;
        private long _byteCount;

        public PcmPendingBuffer(int blockAlign)
        {
            if (blockAlign <= 0) throw new ArgumentOutOfRangeException(nameof(blockAlign));
            _blockAlign = blockAlign;
        }

        public long SampleFrames => _byteCount / _blockAlign;

        public void Enqueue(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            if (bytes.Length % _blockAlign != 0)
                throw new ArgumentException("PCM is not aligned to a complete sample frame.", nameof(bytes));
            if (bytes.Length == 0) return;
            _chunks.Enqueue(bytes);
            _byteCount += bytes.Length;
        }

        public ReadOnlyMemory<byte> DequeueSamples(long maximumSampleFrames)
        {
            if (maximumSampleFrames <= 0 || _chunks.Count == 0)
                return ReadOnlyMemory<byte>.Empty;

            var head = _chunks.Peek();
            var availableBytes = head.Length - _headOffset;
            var requestedBytes = maximumSampleFrames > int.MaxValue / _blockAlign
                ? int.MaxValue
                : checked((int)maximumSampleFrames * _blockAlign);
            requestedBytes -= requestedBytes % _blockAlign;
            var length = Math.Min(availableBytes, requestedBytes);
            if (length == 0) return ReadOnlyMemory<byte>.Empty;

            var result = head.AsMemory(_headOffset, length);
            _headOffset += length;
            _byteCount -= length;
            if (_headOffset == head.Length)
            {
                _chunks.Dequeue();
                _headOffset = 0;
            }
            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool active;
        lock (_stopGate) active = _sessionActive;
        if (active)
            await StopAsync(RecordingStopReason.ApplicationShutdown, CancellationToken.None);
        _lifecycleGate.Dispose();
    }
}
