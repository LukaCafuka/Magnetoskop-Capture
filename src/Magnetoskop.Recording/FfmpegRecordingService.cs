using System.Diagnostics;
using System.IO.Pipes;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Recording;

/// <summary>
/// Synchronized A/V recording through an external FFmpeg process.
/// Raw video frames stream to FFmpeg's stdin; PCM audio streams over a Windows
/// named pipe. FFmpeg performs all encoding and muxing (FFV1/H.264/ProRes),
/// which OpenCV's VideoWriter cannot cover.
/// </summary>
public sealed class FfmpegRecordingService : IRecordingService
{
    private readonly ILogger<FfmpegRecordingService> _logger;

    private Process? _process;
    private NamedPipeServerStream? _audioPipe;
    private CancellationTokenSource? _cts;
    private Task? _videoTask;
    private Task? _audioTask;
    private Task? _stderrTask;
    private Task? _watchdogTask;
    private volatile bool _stopRequested;
    private DateTimeOffset _startedAt;
    private long _framesWritten;
    private long _framesDropped;
    private RecordingStatus _status = new();
    private readonly System.Text.StringBuilder _stderrTail = new();

    public FfmpegRecordingService(ILogger<FfmpegRecordingService> logger)
    {
        _logger = logger;
    }

    public RecordingStatus Status => _status;

    public event EventHandler<RecordingStatus>? StatusChanged;

    /// <summary>Fault the recording when no frame reaches ffmpeg for this long.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Stop (and finalize) the recording when free disk space drops below this.</summary>
    public long MinimumFreeDiskBytes { get; set; } = 500L * 1024 * 1024;

    public async Task StartAsync(
        RecordingProfile profile,
        string outputPath,
        IVideoCaptureService videoSource,
        IAudioCaptureService audioSource,
        CancellationToken cancellationToken = default)
    {
        if (_status.State is RecordingState.Recording or RecordingState.Starting)
        {
            throw new InvalidOperationException("A recording is already in progress.");
        }

        var videoFormat = videoSource.CurrentFormat
            ?? throw new InvalidOperationException("Video capture must be running before recording starts.");
        var audioFormat = audioSource.IsCapturing ? audioSource.CurrentFormat : null;

        var ffmpegPath = FfmpegLocator.Find()
            ?? throw new FileNotFoundException(
                "ffmpeg.exe was not found. Place it next to the application, add it to PATH, " +
                "or configure its location.");

        Publish(new RecordingStatus { State = RecordingState.Starting, OutputPath = outputPath });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Subscribe to the capture streams BEFORE starting ffmpeg so no frames are missed.
        var videoReader = videoSource.Subscribe(capacity: 32);
        var audioReader = audioFormat is not null ? audioSource.Subscribe(capacity: 64) : null;

        // Named pipe for audio.
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

        var args = FfmpegArgumentsBuilder.Build(profile, videoFormat, audioFormat, pipePath, outputPath);
        var argsLine = FfmpegArgumentsBuilder.Join(args);
        _logger.LogInformation("Starting ffmpeg: {Args}", argsLine);

        var process = new Process
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

        if (!process.Start())
        {
            _audioPipe?.Dispose();
            _audioPipe = null;
            throw new IOException("Failed to start the ffmpeg process.");
        }

        _process = process;
        _cts = new CancellationTokenSource();
        _framesWritten = 0;
        _framesDropped = 0;
        _startedAt = DateTimeOffset.UtcNow;
        _stderrTail.Clear();

        var ct = _cts.Token;
        _stderrTask = Task.Run(() => DrainStderrAsync(process, ct), CancellationToken.None);

        // Start feeding video immediately: ffmpeg reads stdin during input probing,
        // and only opens the audio pipe afterwards. Waiting for the pipe first
        // would deadlock.
        _videoTask = Task.Run(() => PumpVideoAsync(videoReader, process, videoFormat, ct), CancellationToken.None);

        // Now wait for ffmpeg to connect to the audio pipe.
        if (_audioPipe is not null)
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _audioPipe.WaitForConnectionAsync(connectTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                await AbortAsync("ffmpeg did not open the audio pipe (is the codec supported by this build?).");
                throw new IOException("ffmpeg did not connect to the audio pipe within 10 s. " +
                                      FfmpegErrorTail());
            }
        }

        if (audioReader is not null && _audioPipe is not null)
        {
            _audioTask = Task.Run(() => PumpAudioAsync(audioReader, _audioPipe, ct), CancellationToken.None);
        }

        Publish(new RecordingStatus
        {
            State = RecordingState.Recording,
            OutputPath = outputPath,
        });
        _logger.LogInformation("Recording started → {Output} ({Profile})", outputPath, profile.DisplayName);

        _stopRequested = false;
        _watchdogTask = Task.Run(() => WatchdogAsync(outputPath, ct), CancellationToken.None);

        // Watch for premature ffmpeg exit.
        _ = process.WaitForExitAsync(CancellationToken.None).ContinueWith(_ =>
        {
            if (_status.State == RecordingState.Recording)
            {
                var message = $"ffmpeg exited unexpectedly (code {process.ExitCode}). {FfmpegErrorTail()}";
                _logger.LogError("{Message}", message);
                _cts?.Cancel();
                Publish(_status with { State = RecordingState.Faulted, Error = message });
            }
        }, TaskScheduler.Default);
    }

    private async Task PumpVideoAsync(
        System.Threading.Channels.ChannelReader<VideoFrame> reader,
        Process process, VideoFormat expectedFormat, CancellationToken ct)
    {
        var stdin = process.StandardInput.BaseStream;
        var expectedBytes = expectedFormat.BytesPerFrame;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                if (frame.Data.Length != expectedBytes)
                {
                    // Format changed mid-recording — cannot feed rawvideo with a different size.
                    Interlocked.Increment(ref _framesDropped);
                    continue;
                }

                await stdin.WriteAsync(frame.Data, ct);
                var written = Interlocked.Increment(ref _framesWritten);

                if (written % 25 == 0)
                {
                    Publish(_status with
                    {
                        Elapsed = DateTimeOffset.UtcNow - _startedAt,
                        VideoFramesWritten = written,
                        VideoFramesDropped = Interlocked.Read(ref _framesDropped),
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Video pipe to ffmpeg broke");
            Publish(_status with { State = RecordingState.Faulted, Error = $"Video pipe failed: {ex.Message}" });
        }
        finally
        {
            try { stdin.Close(); } catch (IOException) { }
        }
    }

    private async Task PumpAudioAsync(
        System.Threading.Channels.ChannelReader<AudioBuffer> reader,
        NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await foreach (var buffer in reader.ReadAllAsync(ct))
            {
                await pipe.WriteAsync(buffer.Data.AsMemory(0, buffer.Length), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Audio pipe to ffmpeg broke");
        }
        finally
        {
            try { pipe.Close(); } catch (IOException) { }
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
            {
                _logger.LogWarning("ffmpeg: {Line}", line);
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Recording health monitor: faults the session when ffmpeg stops consuming
    /// frames (stall) and finalizes it before the output disk runs full.
    /// </summary>
    private async Task WatchdogAsync(string outputPath, CancellationToken ct)
    {
        var lastFrames = -1L;
        var lastProgress = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (_status.State != RecordingState.Recording || _stopRequested) continue;

                // Stall detection: the video pump increments _framesWritten only after
                // ffmpeg accepts the bytes, so a frozen counter means a wedged encoder
                // (or a dead capture source).
                var now = DateTimeOffset.UtcNow;
                var frames = Interlocked.Read(ref _framesWritten);
                if (frames != lastFrames)
                {
                    lastFrames = frames;
                    lastProgress = now;
                }
                else if (now - lastProgress > StallTimeout)
                {
                    await AbortAsync(
                        $"Recording stalled: no frames written for {StallTimeout.TotalSeconds:F0} s. " +
                        FfmpegErrorTail());
                    return;
                }

                // Low-disk: stop gracefully so the file finalizes while space remains.
                if (GetFreeDiskBytes(outputPath) is { } free && free < MinimumFreeDiskBytes)
                {
                    _logger.LogError("Free disk space below {Min} bytes; stopping recording", MinimumFreeDiskBytes);
                    await StopAsync(CancellationToken.None);
                    Publish(_status with
                    {
                        State = RecordingState.Faulted,
                        Error = $"Recording stopped: free disk space fell below " +
                                $"{MinimumFreeDiskBytes / (1024 * 1024)} MB. The file was finalized.",
                    });
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
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
            return null; // network shares etc. — skip the check rather than fault
        }
    }

    private string FfmpegErrorTail()
    {
        lock (_stderrTail)
        {
            return _stderrTail.Length > 0 ? $"ffmpeg output: {_stderrTail}" : "";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null) return;

        _stopRequested = true;
        Publish(_status with { State = RecordingState.Stopping });

        // Stop pumping; the pumps close stdin/pipe which signals EOF to ffmpeg,
        // letting it flush and finalize the container.
        if (_cts is not null) await _cts.CancelAsync();
        await AwaitQuietly(_videoTask);
        await AwaitQuietly(_audioTask);

        var process = _process;
        try
        {
            using var killTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            killTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(killTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ffmpeg did not exit after EOF; killing");
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        await AwaitQuietly(_stderrTask);
        var exitCode = process.HasExited ? process.ExitCode : -1;
        Cleanup();

        if (exitCode == 0)
        {
            Publish(new RecordingStatus
            {
                State = RecordingState.Idle,
                OutputPath = _status.OutputPath,
                VideoFramesWritten = Interlocked.Read(ref _framesWritten),
                Elapsed = DateTimeOffset.UtcNow - _startedAt,
            });
            _logger.LogInformation("Recording finalized: {Output} ({Frames} frames)",
                _status.OutputPath, _framesWritten);
        }
        else
        {
            var message = $"ffmpeg exited with code {exitCode}. {FfmpegErrorTail()}";
            Publish(_status with { State = RecordingState.Faulted, Error = message });
            _logger.LogError("{Message}", message);
        }
    }

    private async Task AbortAsync(string reason)
    {
        _logger.LogError("Recording aborted: {Reason}", reason);
        if (_cts is not null) await _cts.CancelAsync();
        try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        Cleanup();
        Publish(_status with { State = RecordingState.Faulted, Error = reason });
    }

    private void Cleanup()
    {
        _audioPipe?.Dispose();
        _audioPipe = null;
        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
        _videoTask = null;
        _audioTask = null;
        _stderrTask = null;
        _watchdogTask = null;
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null) return;
        try { await task; } catch (Exception) { }
    }

    private void Publish(RecordingStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_status.State is RecordingState.Recording or RecordingState.Starting)
        {
            await StopAsync();
        }
        await AwaitQuietly(_watchdogTask);
        Cleanup();
    }
}