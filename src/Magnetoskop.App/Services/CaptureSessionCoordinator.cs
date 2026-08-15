using System.IO;
using System.Text.Json;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>Sidecar metadata written next to every recording.</summary>
public sealed class RecordingMetadata
{
    public int SchemaVersion { get; set; } = 2;
    public Guid SessionId { get; set; }
    public string? SourceDevice { get; set; }
    public string? VideoDeviceStableId { get; set; }
    public string? AudioDeviceStableId { get; set; }
    public string? VtrDevice { get; set; }
    public string? RecordingProfile { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? StartTimecode { get; set; }
    public string? EndTimecode { get; set; }
    public string? StartCtl { get; set; }
    public string? TimecodeSource { get; set; }
    public string? UserBits { get; set; }
    public long VideoFramesWritten { get; set; }
    public long VideoFramesDropped { get; set; }
    public string? Outcome { get; set; }
    public string? StopReason { get; set; }
    public bool MediaFinalized { get; set; }
    public string? Error { get; set; }
    public RecordingMetrics? Metrics { get; set; }
    public long AudioCalibrationOffset100ns { get; set; }
    public double AudioCalibrationOffsetMilliseconds { get; set; }
    public bool AudioCalibrationConfirmed { get; set; }
    public string? AudioCalibrationKey { get; set; }
    public DateTimeOffset? AudioCalibratedAt { get; set; }
    public string IntegrityScope { get; set; } =
        "Application capture subscriptions through the finalized media file";
    public string UpstreamContinuity { get; set; } =
        "Unknown: OpenCV/DirectShow does not expose an original hardware frame sequence";
    public string? FrameAuditFile { get; set; }
    public long FrameAuditRecordsAccepted { get; set; }
    public long FrameAuditRecordsWritten { get; set; }
    public long FrameAuditRecordsRejected { get; set; }
    public string? FirstCommittedTimecode { get; set; }
    public string? LastCommittedTimecode { get; set; }
    public string? FirstCommittedTimecodeSource { get; set; }
    public string? LastCommittedTimecodeSource { get; set; }
    public double? FirstCommittedObservationAgeMilliseconds { get; set; }
    public double? LastCommittedObservationAgeMilliseconds { get; set; }
    public string? FirstCommittedObservationFreshness { get; set; }
    public string? LastCommittedObservationFreshness { get; set; }
    public double? StartTimecodeAgeMilliseconds { get; set; }
    public double? EndTimecodeAgeMilliseconds { get; set; }

    /// <summary>Interlaced or Progressive — mirrors bitstream scan metadata.</summary>
    public string? ScanType { get; set; }

    /// <summary>TFF / BFF when interlaced; null when progressive.</summary>
    public string? ScanOrder { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
    public VideoInputConfiguration? RequestedVideoInput { get; set; }
    public VideoFormat? ActualVideoInput { get; set; }
    public bool? VideoInputMatchesRequest { get; set; }
    public bool? VideoScanReadbackAvailable { get; set; }
    public bool? VideoFormatAcknowledgmentRequired { get; set; }
    public bool VideoFormatMismatchAcknowledged { get; set; }
    public string? VideoFormatAcknowledgmentKey { get; set; }
    public string? VideoInputFormatMessage { get; set; }
}

/// <summary>Workflow-level choices frozen when strict subscriptions are armed.</summary>
public sealed record CaptureSessionStartOptions
{
    public static CaptureSessionStartOptions Default { get; } = new();
    public RecordingStartOptions Recording { get; init; } = RecordingStartOptions.Default;
    public string? VideoDeviceStableId { get; init; }
    public string? AudioDeviceStableId { get; init; }
    public bool VideoFormatMismatchAcknowledged { get; init; }
    public string? VideoFormatAcknowledgmentKey { get; init; }
}

/// <summary>
/// Application workflow coordination: ties the VTR, the capture services, and the
/// recorder together. Responsible for the capture workflow (preview must run, tape
/// checks, optional auto-play), timecode-stamped file naming, and sidecar metadata.
/// </summary>
public sealed class CaptureSessionCoordinator
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly TimeSpan TimecodeStaleAfter = TimeSpan.FromSeconds(1);

    private readonly IVtrController _vtr;
    private readonly IVideoCaptureService _video;
    private readonly IAudioCaptureService _audio;
    private readonly IRecordingService _recorder;
    private readonly ILogger<CaptureSessionCoordinator> _logger;
    private readonly VtrObservationHistory _vtrObservations = new();
    private readonly object _metadataGate = new();

    private RecordingMetadata? _activeMetadata;
    private string? _activeOutputPath;
    private string? _activePartialMetadataPath;
    private string? _activeFinalMetadataPath;
    private FrameAuditWriter? _activeFrameAudit;
    private Task? _completionMonitor;
    private Task? _auditHealthMonitor;
    private readonly SemaphoreSlim _artifactGate = new(1, 1);
    private long? _firstCommittedCaptureTimestamp100ns;
    private int _auditStopRequested;

    /// <summary>How long to wait for Playing + servo lock after an auto-play command.</summary>
    public TimeSpan AutoPlayTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public CaptureSessionCoordinator(
        IVtrController vtr,
        IVideoCaptureService video,
        IAudioCaptureService audio,
        IRecordingService recorder,
        ILogger<CaptureSessionCoordinator> logger)
    {
        _vtr = vtr;
        _video = video;
        _audio = audio;
        _recorder = recorder;
        _logger = logger;
        _recorder.VideoFrameCommitted += OnVideoFrameCommitted;
        _vtr.TimeChanged += OnVtrTimeChanged;
    }

    /// <summary>Warn when the output volume has less free space than this (10 GB —
    /// roughly 10 minutes of FFV1 SD material).</summary>
    public long LowDiskWarningBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Non-fatal issues detected before a recording starts. When
    /// <paramref name="autoPlayEnabled"/> is true the "transport is not Playing"
    /// warning is suppressed, because auto-play will start the deck itself.
    /// </summary>
    public IReadOnlyList<string> PreflightWarnings(bool autoPlayEnabled = false, string? outputDirectory = null)
    {
        var warnings = new List<string>();

        if (Recording.FfmpegLocator.Find() is null)
        {
            warnings.Add("ffmpeg.exe not found — recording will fail. Install FFmpeg or place ffmpeg.exe next to the application.");
        }
        if (outputDirectory is not null && GetFreeDiskBytes(outputDirectory) is { } free
            && free < LowDiskWarningBytes)
        {
            warnings.Add($"Only {free / (1024.0 * 1024 * 1024):F1} GB free on the output drive — " +
                         "archival recordings may not fit. The recording stops automatically before the disk fills.");
        }
        if (!_video.IsCapturing)
        {
            warnings.Add("Video capture is not running.");
        }
        if (!_audio.IsCapturing)
        {
            warnings.Add("Audio capture is not running — the file will have no audio track.");
        }
        if (_vtr.IsConnected)
        {
            var status = _vtr.CurrentStatus;
            if (status.TapeOut) warnings.Add("The recorder reports no tape loaded.");
            if (status.Transport != TransportState.Playing && !autoPlayEnabled)
            {
                warnings.Add($"The recorder transport is '{status.Transport}', not Playing.");
            }
        }
        else
        {
            warnings.Add("No recorder connected — timecode metadata will be unavailable.");
        }

        return warnings;
    }

    private static long? GetFreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Issues Play when the deck is connected, loaded, and not already playing,
    /// then waits (up to <see cref="AutoPlayTimeout"/>) for Playing + servo lock.
    /// Returns false when playback could not be confirmed in time.
    /// </summary>
    public async Task<bool> EnsurePlayingAsync(CancellationToken ct = default)
    {
        if (!_vtr.IsConnected || _vtr.CurrentStatus.TapeOut)
        {
            return false;
        }
        if (_vtr.CurrentStatus.Transport == TransportState.Playing)
        {
            return true;
        }

        _logger.LogInformation("Auto-play: sending Play before recording");
        await _vtr.SendTransportCommandAsync(TransportCommand.Play, ct);

        var deadline = DateTime.UtcNow + AutoPlayTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = _vtr.CurrentStatus;
            if (status.Transport == TransportState.Playing && status.ServoLock)
            {
                return true;
            }
            await Task.Delay(50, ct);
        }

        _logger.LogWarning("Auto-play: deck did not reach Playing with servo lock within {Timeout}",
            AutoPlayTimeout);
        return _vtr.CurrentStatus.Transport == TransportState.Playing;
    }

    /// <summary>
    /// Builds the output file name: capture_yyyyMMdd_HHmmss[_TChh-mm-ss-ff].ext
    /// </summary>
    public string BuildOutputPath(string directory, RecordingProfile profile)
    {
        var snapshot = FreshTimeSnapshot();
        return BuildOutputPath(directory, profile, snapshot);
    }

    private static string BuildOutputPath(
        string directory,
        RecordingProfile profile,
        SessionTimeSnapshot? snapshot)
    {
        var name = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}";
        var tc = snapshot?.Primary.Value;
        if (tc is not null)
        {
            name += $"_TC{tc.Value.Hours:D2}-{tc.Value.Minutes:D2}-{tc.Value.Seconds:D2}-{tc.Value.Frames:D2}";
        }
        return Path.Combine(directory, $"{name}.{profile.Container}");
    }

    private SessionTimeSnapshot? FreshTimeSnapshot()
    {
        if (!_vtr.IsConnected) return null;
        var capturedAt = DateTimeOffset.UtcNow;
        var capturedTimestamp100ns = CaptureMonotonicClock.GetTimestamp100ns();
        var information = _vtr.CurrentTime;
        var primary = information.GetPrimaryObservation(
            capturedAt,
            capturedTimestamp100ns,
            TimecodeStaleAfter);
        if (primary is null) return null;

        var ctl = Fresh(information.CtlObservation, capturedAt, capturedTimestamp100ns);
        var userBits = primary.Value.Source is TimecodeSource.Vitc or TimecodeSource.HoldVitc
            ? Fresh(information.VitcUserBitsObservation, capturedAt, capturedTimestamp100ns)
                ?? Fresh(information.LtcUserBitsObservation, capturedAt, capturedTimestamp100ns)
            : Fresh(information.LtcUserBitsObservation, capturedAt, capturedTimestamp100ns)
                ?? Fresh(information.VitcUserBitsObservation, capturedAt, capturedTimestamp100ns);
        return new SessionTimeSnapshot(
            information,
            primary.Value,
            ctl,
            userBits,
            capturedAt,
            capturedTimestamp100ns);
    }

    private static TimeObservation<T>? Fresh<T>(
        TimeObservation<T>? observation,
        DateTimeOffset now,
        long nowTimestamp100ns)
    {
        return observation is { } value
               && value.FreshnessAt(nowTimestamp100ns, now) == ObservationFreshness.Fresh
            ? value
            : null;
    }

    private sealed record SessionTimeSnapshot(
        TimeInformation Information,
        TimeObservation<Timecode> Primary,
        TimeObservation<Timecode>? Ctl,
        TimeObservation<UserBits>? UserBits,
        DateTimeOffset CapturedAt,
        long CapturedTimestamp100ns);

    /// <summary>Starts a recording session with timecode metadata capture.</summary>
    public Task<string> StartRecordingAsync(
        string outputDirectory, RecordingProfile profile,
        string? videoDeviceName, CancellationToken ct = default)
        => StartRecordingAsync(
            outputDirectory,
            profile,
            videoDeviceName,
            CaptureSessionStartOptions.Default,
            ct);

    /// <summary>Starts a recording with the selected device-pair timing calibration.</summary>
    public async Task<string> StartRecordingAsync(
        string outputDirectory,
        RecordingProfile profile,
        string? videoDeviceName,
        RecordingStartOptions startOptions,
        CancellationToken ct = default)
        => await StartRecordingAsync(
            outputDirectory,
            profile,
            videoDeviceName,
            new CaptureSessionStartOptions { Recording = startOptions },
            ct);

    /// <summary>
    /// Starts a session only after the actual driver format has either matched the
    /// operator request or the exact mismatch has been acknowledged.
    /// </summary>
    public async Task<string> StartRecordingAsync(
        string outputDirectory,
        RecordingProfile profile,
        string? videoDeviceName,
        CaptureSessionStartOptions sessionOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        var startOptions = sessionOptions.Recording;
        ArgumentNullException.ThrowIfNull(startOptions);

        var videoFormatStatus = (_video as IConfigurableVideoCaptureService)?.FormatStatus;
        if (videoFormatStatus is { Requested.IsExplicit: false })
        {
            throw new InvalidOperationException(
                "Select the video input standard and scan mode before recording.");
        }
        if (videoFormatStatus is { AcknowledgmentRequired: true } status)
        {
            if (status.Actual is null)
            {
                throw new InvalidOperationException(
                    "Open the preview and obtain a driver-format readback before recording. "
                    + status.Message);
            }
            if (string.IsNullOrWhiteSpace(sessionOptions.VideoDeviceStableId))
            {
                throw new InvalidOperationException(
                    "A stable video-device identity is required for format acknowledgement.");
            }

            var expectedAcknowledgment = status.BuildAcknowledgmentKey(
                sessionOptions.VideoDeviceStableId);
            if (!sessionOptions.VideoFormatMismatchAcknowledged
                || !string.Equals(
                    sessionOptions.VideoFormatAcknowledgmentKey,
                    expectedAcknowledgment,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The exact capture-device format readback has not been acknowledged "
                    + $"for this stable device. {status.Message}");
            }
        }

        var requestedAt = DateTimeOffset.Now;
        _vtrObservations.Clear();
        if (_vtr.IsConnected)
        {
            _vtrObservations.Observe(_vtr.CurrentTime, _vtr.CurrentStatus);
        }
        var time = FreshTimeSnapshot();
        var outputPath = MakeSessionPathUnique(
            BuildOutputPath(outputDirectory, profile, time));
        var format = _video.CurrentFormat;
        var finalMetadataPath = Path.ChangeExtension(outputPath, ".json");
        var partialMetadataPath = finalMetadataPath + ".partial";
        var auditPath = Path.ChangeExtension(outputPath, ".frames.jsonl");
        var timeAge = time is null
            ? (double?)null
            : time.Primary.AgeAt(time.CapturedTimestamp100ns, time.CapturedAt).TotalMilliseconds;

        await _artifactGate.WaitAsync(ct);
        try
        {
            if (_activeMetadata is not null)
                throw new InvalidOperationException("The previous recording artifacts are still being finalized.");

            _activeMetadata = new RecordingMetadata
            {
                SessionId = Guid.NewGuid(),
                SourceDevice = videoDeviceName,
                VideoDeviceStableId = sessionOptions.VideoDeviceStableId,
                AudioDeviceStableId = sessionOptions.AudioDeviceStableId,
                VtrDevice = _vtr.IsConnected ? _vtr.DeviceDescription : null,
                RecordingProfile = profile.DisplayName,
                AudioCalibrationOffset100ns = startOptions.AudioOffset100ns,
                AudioCalibrationOffsetMilliseconds =
                    TimeSpan.FromTicks(startOptions.AudioOffset100ns).TotalMilliseconds,
                AudioCalibrationConfirmed = startOptions.IsCalibrated,
                AudioCalibrationKey = startOptions.CalibrationKey,
                AudioCalibratedAt = startOptions.CalibratedAt,
                RequestedVideoInput = videoFormatStatus?.Requested,
                ActualVideoInput = videoFormatStatus?.Actual,
                VideoInputMatchesRequest = videoFormatStatus?.MatchesRequested,
                VideoScanReadbackAvailable = videoFormatStatus?.ScanReadbackAvailable,
                VideoFormatAcknowledgmentRequired = videoFormatStatus?.AcknowledgmentRequired,
                VideoFormatMismatchAcknowledged =
                    sessionOptions.VideoFormatMismatchAcknowledged,
                VideoFormatAcknowledgmentKey =
                    sessionOptions.VideoFormatAcknowledgmentKey,
                VideoInputFormatMessage = videoFormatStatus?.Message,
                RequestedAt = requestedAt,
                StartedAt = requestedAt,
                StartTimecode = time?.Primary.Value.ToString(),
                StartCtl = time?.Ctl?.Value.ToString(),
                TimecodeSource = time?.Primary.Source.ToString(),
                UserBits = time?.UserBits?.Value.ToString(),
                StartTimecodeAgeMilliseconds = timeAge,
                FrameAuditFile = Path.GetFileName(auditPath),
                ScanType = format is null
                    ? null
                    : Recording.FfmpegArgumentsBuilder.WillDeinterlace(format, profile) || !format.Interlaced
                        ? "Progressive"
                        : "Interlaced",
                ScanOrder = format is { Interlaced: true }
                            && !Recording.FfmpegArgumentsBuilder.WillDeinterlace(format, profile)
                    ? (format.TopFieldFirst ? "TFF" : "BFF")
                    : null,
                Width = format?.Width,
                Height = format?.Height,
                FrameRate = format is null
                    ? null
                    : Recording.FfmpegArgumentsBuilder.OutputFrameRate(format, profile),
            };
            _activeOutputPath = outputPath;
            _activeFinalMetadataPath = finalMetadataPath;
            _activePartialMetadataPath = partialMetadataPath;
            _activeFrameAudit = new FrameAuditWriter(auditPath);
            _firstCommittedCaptureTimestamp100ns = null;
            _auditStopRequested = 0;

            await AtomicJsonFile.WriteAsync(
                partialMetadataPath, _activeMetadata, MetadataJsonOptions, ct);
        }
        catch
        {
            // No recorder has been armed yet. Roll back all in-memory ownership so
            // an audit/open or initial-partial failure cannot poison later sessions.
            var audit = _activeFrameAudit;
            ClearActiveArtifacts();
            if (audit is not null) await audit.DisposeAsync();
            _vtrObservations.Clear();
            throw;
        }
        finally
        {
            _artifactGate.Release();
        }

        var recorderStarted = false;
        try
        {
            await _recorder.StartAsync(
                profile, outputPath, _video, _audio, startOptions, ct);
            recorderStarted = true;
            if (_activeMetadata is not null)
            {
                _activeMetadata.StartedAt = DateTimeOffset.Now;
                await AtomicJsonFile.WriteAsync(
                    partialMetadataPath, _activeMetadata, MetadataJsonOptions, ct);
            }

            var completion = _recorder.Completion;
            _completionMonitor = MonitorCompletionAsync(completion);
            _auditHealthMonitor = MonitorAuditHealthAsync(_activeFrameAudit!, completion);
            _logger.LogInformation("Session recording started: {Path} (start TC {Tc})",
                outputPath, _activeMetadata?.StartTimecode ?? "n/a");
            return outputPath;
        }
        catch (Exception ex)
        {
            var failed = await StopRecorderAfterSessionStartFailureAsync(
                outputPath, requestedAt, startOptions, recorderStarted, ex);
            await FinalizeArtifactsAsync(failed, CancellationToken.None);
            throw;
        }
    }

    private async Task<RecordingResult> StopRecorderAfterSessionStartFailureAsync(
        string outputPath,
        DateTimeOffset requestedAt,
        RecordingStartOptions startOptions,
        bool recorderStarted,
        Exception startupException)
    {
        try
        {
            if (recorderStarted
                || _recorder.Status.State is RecordingState.Starting
                or RecordingState.Recording
                or RecordingState.Stopping)
            {
                var stopped = await _recorder.StopAsync(
                    RecordingStopReason.InternalFailure,
                    CancellationToken.None);
                return stopped with
                {
                    Outcome = RecordingOutcome.Faulted,
                    StopReason = RecordingStopReason.InternalFailure,
                    Error = $"Session evidence initialization failed after media was armed: {startupException.Message}",
                };
            }

            if (_recorder.Completion.IsCompletedSuccessfully)
            {
                var terminal = await _recorder.Completion;
                if (string.Equals(terminal.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
                    return terminal;
            }
        }
        catch (Exception stopException)
        {
            _logger.LogError(
                stopException,
                "Could not stop the recorder after session evidence initialization failed");
        }

        return new RecordingResult
        {
            Outcome = RecordingOutcome.Faulted,
            StopReason = RecordingStopReason.StartupFailure,
            OutputPath = outputPath,
            StartedAt = requestedAt,
            EndedAt = DateTimeOffset.Now,
            MediaFinalized = false,
            StartOptions = startOptions,
            Error = startupException.Message,
        };
    }

    private static string MakeSessionPathUnique(string requestedPath)
    {
        if (!SessionArtifactsExist(requestedPath)) return requestedPath;

        var directory = Path.GetDirectoryName(requestedPath)!;
        var stem = Path.GetFileNameWithoutExtension(requestedPath);
        var extension = Path.GetExtension(requestedPath);
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{suffix:D3}{extension}");
            if (!SessionArtifactsExist(candidate)) return candidate;
        }

        throw new IOException("Could not allocate a unique recording filename.");
    }

    private static bool SessionArtifactsExist(string mediaPath)
    {
        var summary = Path.ChangeExtension(mediaPath, ".json");
        return File.Exists(mediaPath)
               || File.Exists(summary)
               || File.Exists(summary + ".partial")
               || File.Exists(Path.ChangeExtension(mediaPath, ".frames.jsonl"));
    }

    /// <summary>Stops the recording and writes the sidecar metadata JSON.</summary>
    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        var result = await _recorder.StopAsync(RecordingStopReason.UserRequested, ct);
        await FinalizeArtifactsAsync(result, ct);
        if (_completionMonitor is not null)
        {
            try { await _completionMonitor.WaitAsync(ct); } catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>Stops a live session during application shutdown and preserves its terminal metadata.</summary>
    public async Task StopForShutdownAsync(CancellationToken ct = default)
    {
        if (_activeMetadata is null) return;
        var result = await _recorder.StopAsync(RecordingStopReason.ApplicationShutdown, ct);
        await FinalizeArtifactsAsync(result, ct);
    }

    private async Task MonitorCompletionAsync(Task<RecordingResult> completion)
    {
        try
        {
            var result = await completion;
            await FinalizeArtifactsAsync(result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording completion monitor failed");
        }
    }

    private async Task MonitorAuditHealthAsync(
        FrameAuditWriter audit,
        Task<RecordingResult> recordingCompletion)
    {
        var finished = await Task.WhenAny(audit.Overflowed, audit.Faulted, recordingCompletion);
        if (ReferenceEquals(finished, recordingCompletion)) return;

        if (Interlocked.Exchange(ref _auditStopRequested, 1) != 0) return;
        var reason = audit.Rejected > 0 ? "queue overflow" : "writer failure";
        _logger.LogError("Frame audit {Reason}; stopping the recording as incomplete", reason);
        try
        {
            await _recorder.StopAsync(RecordingStopReason.FrameAuditOverflow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop recording after frame-audit failure");
        }
    }

    private void OnVideoFrameCommitted(object? sender, VideoFrameCommitted frame)
    {
        var audit = _activeFrameAudit;
        var metadata = _activeMetadata;
        if (audit is null || metadata is null) return;

        var captureTimestamp100ns = frame.CaptureTimestamp.Ticks;
        var association = _vtrObservations.FindNearestPreceding(captureTimestamp100ns);
        var observed = association?.Value;
        var age = association?.AgeAt(captureTimestamp100ns);
        var freshness = association?.FreshnessAt(captureTimestamp100ns)
            ?? ObservationFreshness.Unavailable;
        var estimated = EstimateTimecode(association, captureTimestamp100ns);
        var effective = estimated ?? observed;

        double captureOffsetMilliseconds;
        lock (_metadataGate)
        {
            _firstCommittedCaptureTimestamp100ns ??= captureTimestamp100ns;
            captureOffsetMilliseconds = TimeSpan.FromTicks(
                captureTimestamp100ns - _firstCommittedCaptureTimestamp100ns.Value).TotalMilliseconds;
            if (metadata.FirstCommittedTimecode is null)
            {
                metadata.FirstCommittedTimecode = effective?.ToString();
                metadata.FirstCommittedTimecodeSource = association?.Source.ToString();
                metadata.FirstCommittedObservationAgeMilliseconds = age?.TotalMilliseconds;
                metadata.FirstCommittedObservationFreshness = freshness.ToString();
            }
            metadata.LastCommittedTimecode = effective?.ToString();
            metadata.LastCommittedTimecodeSource = association?.Source.ToString();
            metadata.LastCommittedObservationAgeMilliseconds = age?.TotalMilliseconds;
            metadata.LastCommittedObservationFreshness = freshness.ToString();
        }

        if (!audit.TryWrite(new FrameAuditRecord
            {
                RecordingFrameNumber = frame.SequenceNumber,
                SourceFrameNumber = frame.SourceFrameNumber,
                CaptureTimestamp100Ns = captureTimestamp100ns,
                CaptureOffsetMilliseconds = captureOffsetMilliseconds,
                ObservedTimecode = observed?.ToString(),
                EstimatedTimecode = estimated?.ToString(),
                TimecodeSource = association?.Source.ToString(),
                ObservationAgeMilliseconds = age?.TotalMilliseconds,
                TimecodeFreshness = freshness.ToString(),
                IsEstimated = estimated is not null,
                ServoLocked = association?.ServoLocked ?? false,
                ServoStateFresh = association?.IsServoStateFreshAt(captureTimestamp100ns) ?? false,
                TapeReverse = association?.TapeReverse ?? false,
                TransportState = association?.Transport.ToString(),
            })
            && Interlocked.Exchange(ref _auditStopRequested, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _recorder.StopAsync(RecordingStopReason.FrameAuditOverflow, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stop recording after frame-audit overflow");
                }
            });
        }
    }

    private Timecode? EstimateTimecode(
        VtrFrameAssociation? association,
        long captureTimestamp100ns)
    {
        if (association is null || !association.CanInterpolateAt(captureTimestamp100ns))
            return null;

        var actualFps = _video.CurrentFormat?.FrameRate ?? 25;
        var nominalFps = (int)Math.Round(actualFps);
        if (nominalFps <= 0) nominalFps = 25;
        var age = association.AgeAt(captureTimestamp100ns);
        var advance = (long)Math.Round(
            age.TotalSeconds * actualFps,
            MidpointRounding.AwayFromZero);
        return association.Value.AddFrames(advance, nominalFps);
    }

    private void OnVtrTimeChanged(object? sender, TimeInformation information)
        => _vtrObservations.Observe(information, _vtr.CurrentStatus);

    private async Task FinalizeArtifactsAsync(RecordingResult result, CancellationToken ct)
    {
        await _artifactGate.WaitAsync(ct);
        try
        {
            if (_activeMetadata is null || _activeOutputPath is null) return;

            var time = FreshTimeSnapshot();
            if (result.StartedAt != default)
            {
                // The recorder owns the authoritative arm time: strict media
                // subscriptions are installed only after FFmpeg is ready.
                _activeMetadata.StartedAt = result.StartedAt;
            }
            _activeMetadata.EndedAt = result.EndedAt == default ? DateTimeOffset.Now : result.EndedAt;
            _activeMetadata.EndTimecode = time?.Primary.Value.ToString();
            _activeMetadata.EndTimecodeAgeMilliseconds = time is null
                ? null
                : time.Primary.AgeAt(
                    time.CapturedTimestamp100ns,
                    time.CapturedAt).TotalMilliseconds;
            _activeMetadata.VideoFramesWritten = result.Metrics.VideoFramesWritten;
            _activeMetadata.VideoFramesDropped = result.Metrics.VideoFramesDropped;
            _activeMetadata.Outcome = result.Outcome.ToString();
            _activeMetadata.StopReason = result.StopReason.ToString();
            _activeMetadata.MediaFinalized = result.MediaFinalized;
            _activeMetadata.Error = result.Error;
            _activeMetadata.Metrics = result.Metrics;
            _activeMetadata.AudioCalibrationOffset100ns = result.StartOptions.AudioOffset100ns;
            _activeMetadata.AudioCalibrationOffsetMilliseconds =
                TimeSpan.FromTicks(result.StartOptions.AudioOffset100ns).TotalMilliseconds;
            _activeMetadata.AudioCalibrationConfirmed = result.StartOptions.IsCalibrated;
            _activeMetadata.AudioCalibrationKey = result.StartOptions.CalibrationKey;
            _activeMetadata.AudioCalibratedAt = result.StartOptions.CalibratedAt;

            if (_activeFrameAudit is not null)
            {
                try { await _activeFrameAudit.CompleteAsync(ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Frame audit could not be finalized");
                    _activeMetadata.Error = string.IsNullOrWhiteSpace(_activeMetadata.Error)
                        ? $"Frame audit failed: {ex.Message}"
                        : $"{_activeMetadata.Error}; frame audit failed: {ex.Message}";
                    // A finalized media file with an unreliable/missing audit is
                    // playable, but it cannot make a completeness claim.
                    _activeMetadata.Outcome = RecordingOutcome.Incomplete.ToString();
                    _activeMetadata.StopReason = RecordingStopReason.FrameAuditOverflow.ToString();
                }

                _activeMetadata.FrameAuditRecordsAccepted = _activeFrameAudit.Accepted;
                _activeMetadata.FrameAuditRecordsWritten = _activeFrameAudit.Written;
                _activeMetadata.FrameAuditRecordsRejected = _activeFrameAudit.Rejected;
                if (_activeFrameAudit.Rejected > 0)
                {
                    const string auditLoss =
                        "One or more committed-frame audit records were rejected.";
                    _activeMetadata.Error = string.IsNullOrWhiteSpace(_activeMetadata.Error)
                        ? auditLoss
                        : $"{_activeMetadata.Error}; {auditLoss}";
                    if (string.Equals(
                            _activeMetadata.Outcome,
                            RecordingOutcome.Completed.ToString(),
                            StringComparison.Ordinal))
                    {
                        _activeMetadata.Outcome = RecordingOutcome.Incomplete.ToString();
                        _activeMetadata.StopReason =
                            RecordingStopReason.FrameAuditOverflow.ToString();
                    }
                }
            }

            var finalWritten = false;
            try
            {
                // Refreshing the crash-recovery artifact is best effort. A locked
                // partial must never prevent the independent final JSON attempt.
                if (_activePartialMetadataPath is not null)
                {
                    try
                    {
                        await AtomicJsonFile.WriteAsync(
                            _activePartialMetadataPath, _activeMetadata, MetadataJsonOptions, ct);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not refresh partial sidecar metadata");
                    }
                }

                if (_activeFinalMetadataPath is not null)
                {
                    try
                    {
                        await AtomicJsonFile.WriteAsync(
                            _activeFinalMetadataPath, _activeMetadata, MetadataJsonOptions, ct);
                        finalWritten = true;
                        _logger.LogInformation(
                            "Sidecar metadata written: {Path}", _activeFinalMetadataPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to finalize sidecar metadata; partial metadata was retained");
                    }
                }

                if (finalWritten
                    && _activePartialMetadataPath is not null
                    && File.Exists(_activePartialMetadataPath))
                {
                    try { File.Delete(_activePartialMetadataPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Final sidecar exists, but its partial predecessor could not be removed");
                    }
                }
            }
            finally
            {
                if (_activeFrameAudit is not null) await _activeFrameAudit.DisposeAsync();
            }

            ClearActiveArtifacts();
            _vtrObservations.Clear();
        }
        finally
        {
            _artifactGate.Release();
        }
    }

    private void ClearActiveArtifacts()
    {
        _activeMetadata = null;
        _activeOutputPath = null;
        _activePartialMetadataPath = null;
        _activeFinalMetadataPath = null;
        _activeFrameAudit = null;
        _firstCommittedCaptureTimestamp100ns = null;
        _completionMonitor = null;
        _auditHealthMonitor = null;
    }
}
