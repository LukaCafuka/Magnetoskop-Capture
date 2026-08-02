using System.IO;
using System.Text.Json;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>Sidecar metadata written next to every recording.</summary>
public sealed class RecordingMetadata
{
    public string? SourceDevice { get; set; }
    public string? VtrDevice { get; set; }
    public string? RecordingProfile { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? StartTimecode { get; set; }
    public string? EndTimecode { get; set; }
    public string? StartCtl { get; set; }
    public string? TimecodeSource { get; set; }
    public string? UserBits { get; set; }
    public long VideoFramesWritten { get; set; }
    public long VideoFramesDropped { get; set; }

    /// <summary>Interlaced or Progressive — mirrors bitstream scan metadata.</summary>
    public string? ScanType { get; set; }

    /// <summary>TFF / BFF when interlaced; null when progressive.</summary>
    public string? ScanOrder { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
}

/// <summary>
/// Application workflow coordination: ties the VTR, the capture services, and the
/// recorder together. Responsible for the capture workflow (preview must run, tape
/// checks, optional auto-play), timecode-stamped file naming, and sidecar metadata.
/// </summary>
public sealed class CaptureSessionCoordinator
{
    private readonly IVtrController _vtr;
    private readonly IVideoCaptureService _video;
    private readonly IAudioCaptureService _audio;
    private readonly IRecordingService _recorder;
    private readonly ILogger<CaptureSessionCoordinator> _logger;

    private RecordingMetadata? _activeMetadata;
    private string? _activeOutputPath;

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
        var name = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}";
        var tc = CurrentTimecode();
        if (tc is not null)
        {
            name += $"_TC{tc.Value.Hours:D2}-{tc.Value.Minutes:D2}-{tc.Value.Seconds:D2}-{tc.Value.Frames:D2}";
        }
        return Path.Combine(directory, $"{name}.{profile.Container}");
    }

    private Timecode? CurrentTimecode()
    {
        if (!_vtr.IsConnected) return null;
        var time = _vtr.CurrentTime;
        return time.PrimarySource switch
        {
            TimecodeSource.Vitc => time.Vitc ?? time.Ltc ?? time.Ctl,
            _ => time.Ltc ?? time.Vitc ?? time.Ctl,
        };
    }

    /// <summary>Starts a recording session with timecode metadata capture.</summary>
    public async Task<string> StartRecordingAsync(
        string outputDirectory, RecordingProfile profile,
        string? videoDeviceName, CancellationToken ct = default)
    {
        var outputPath = BuildOutputPath(outputDirectory, profile);
        var time = _vtr.IsConnected ? _vtr.CurrentTime : null;
        var format = _video.CurrentFormat;

        _activeMetadata = new RecordingMetadata
        {
            SourceDevice = videoDeviceName,
            VtrDevice = _vtr.IsConnected ? _vtr.DeviceDescription : null,
            RecordingProfile = profile.DisplayName,
            StartedAt = DateTimeOffset.Now,
            StartTimecode = CurrentTimecode()?.ToString(),
            StartCtl = time?.Ctl?.ToString(),
            TimecodeSource = time?.PrimarySource.ToString(),
            UserBits = (time?.LtcUserBits ?? time?.VitcUserBits)?.ToString(),
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

        await _recorder.StartAsync(profile, outputPath, _video, _audio, ct);
        _logger.LogInformation("Session recording started: {Path} (start TC {Tc})",
            outputPath, _activeMetadata.StartTimecode ?? "n/a");
        return outputPath;
    }

    /// <summary>Stops the recording and writes the sidecar metadata JSON.</summary>
    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        await _recorder.StopAsync(ct);

        if (_activeMetadata is not null && _activeOutputPath is not null)
        {
            _activeMetadata.EndedAt = DateTimeOffset.Now;
            _activeMetadata.EndTimecode = CurrentTimecode()?.ToString();
            _activeMetadata.VideoFramesWritten = _recorder.Status.VideoFramesWritten;
            _activeMetadata.VideoFramesDropped = _recorder.Status.VideoFramesDropped;

            var sidecarPath = Path.ChangeExtension(_activeOutputPath, ".json");
            try
            {
                await File.WriteAllTextAsync(sidecarPath,
                    JsonSerializer.Serialize(_activeMetadata, new JsonSerializerOptions { WriteIndented = true }), ct);
                _logger.LogInformation("Sidecar metadata written: {Path}", sidecarPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to write sidecar metadata");
            }
        }

        _activeMetadata = null;
        _activeOutputPath = null;
    }
}