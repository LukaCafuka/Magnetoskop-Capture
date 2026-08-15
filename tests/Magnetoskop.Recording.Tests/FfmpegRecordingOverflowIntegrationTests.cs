using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Recording.Tests;

public sealed class FfmpegRecordingOverflowIntegrationTests
{
    private static readonly VideoFormat VideoFormat = new()
    {
        Width = 64,
        Height = 48,
        FrameRate = 25,
        PixelFormat = VideoPixelFormat.Bgr24,
        Interlaced = true,
        TopFieldFirst = true,
    };

    private static readonly AudioFormat AudioFormat = new()
    {
        SampleRate = 48_000,
        Channels = 2,
        BitsPerSample = 16,
    };

    private static readonly VideoFormat LargeVideoFormat = VideoFormat with
    {
        Width = 1920,
        Height = 1080,
    };

    [SkippableFact]
    public async Task StrictQueueOverflow_FinalizesPlayableContiguousPrefix()
    {
        var ffmpeg = FfmpegLocator.Find();
        Skip.If(ffmpeg is null, "ffmpeg.exe not found on this machine");

        var directory = Directory.CreateTempSubdirectory("magnetoskop-overflow").FullName;
        var output = Path.Combine(directory, "overflow.mkv");
        var alignedStart = CaptureMonotonicClock.GetTimestamp100ns()
            - TimeSpan.FromSeconds(1).Ticks;
        await using var video = new BurstVideoSource(alignedStart, itemCount: 5);
        await using var audio = new BurstAudioSource(alignedStart, itemCount: 17);
        await using var recorder = new FfmpegRecordingService(
            NullLogger<FfmpegRecordingService>.Instance)
        {
            // Production code retains its hard minimums: four frames and sixteen
            // audio packets. Each source deliberately emits exactly one item more.
            StrictQueueDuration = TimeSpan.FromMilliseconds(1),
            StallTimeout = TimeSpan.FromSeconds(5),
            MinimumFreeDiskBytes = 0,
        };

        try
        {
            await recorder.StartAsync(
                RecordingProfile.CreateFfv1Archival(), output, video, audio);
            var result = await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(RecordingOutcome.Incomplete, result.Outcome);
            Assert.Equal(RecordingStopReason.QueueOverflow, result.StopReason);
            Assert.True(result.MediaFinalized, result.Error);
            Assert.Equal(5, result.Metrics.VideoQueuePublishAttempts);
            Assert.Equal(4, result.Metrics.VideoQueueAccepted);
            Assert.Equal(4, result.Metrics.VideoQueueConsumed);
            Assert.Equal(1, result.Metrics.VideoQueueRejectedNew);
            Assert.Equal(17, result.Metrics.AudioQueuePublishAttempts);
            Assert.Equal(16, result.Metrics.AudioQueueAccepted);
            Assert.Equal(16, result.Metrics.AudioQueueConsumed);
            Assert.Equal(1, result.Metrics.AudioQueueRejectedNew);
            Assert.Equal(4, result.Metrics.VideoFramesWritten);

            var file = new FileInfo(output);
            Assert.True(file.Exists && file.Length > 0,
                "The accepted prefix did not produce a playable partial artifact.");
            await AssertPlayablePrefixAsync(ffmpeg!, output, result.Metrics.VideoFramesWritten);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [SkippableFact]
    public async Task EofRemuxWithoutDuplicateSpace_PreservesOriginalAndRootFailure()
    {
        var ffmpeg = FfmpegLocator.Find();
        Skip.If(ffmpeg is null, "ffmpeg.exe not found on this machine");

        var directory = Directory.CreateTempSubdirectory("magnetoskop-remux-space").FullName;
        var output = Path.Combine(directory, "retained-original.mkv");
        var alignedStart = CaptureMonotonicClock.GetTimestamp100ns()
            - TimeSpan.FromSeconds(1).Ticks;
        await using var video = new FormatFaultVideoSource(alignedStart);
        await using var audio = new BurstAudioSource(alignedStart, itemCount: 16);
        await using var recorder = new FfmpegRecordingService(
            NullLogger<FfmpegRecordingService>.Instance)
        {
            StrictQueueDuration = TimeSpan.FromMilliseconds(1),
            StallTimeout = TimeSpan.FromSeconds(10),
            MinimumFreeDiskBytes = 0,
            FinalizationFreeSpaceMarginBytes = long.MaxValue,
        };

        try
        {
            await recorder.StartAsync(
                RecordingProfile.CreateFfv1Archival(), output, video, audio);
            var result = await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(RecordingOutcome.Incomplete, result.Outcome);
            Assert.Equal(RecordingStopReason.FormatChanged, result.StopReason);
            Assert.True(result.MediaFinalized, result.Error);
            Assert.Contains("could not be trimmed", result.Error);
            Assert.True(result.Metrics.AudioSamplesInterleaveLookaheadSubmitted > 0);
            Assert.Equal(0, result.Metrics.AudioSamplesInterleaveLookaheadTrimmed);
            Assert.Equal(result.Metrics.AudioSamplesSubmittedToEncoder,
                result.Metrics.AudioSamplesWritten);
            Assert.True(File.Exists(output) && new FileInfo(output).Length > 0);

            var decode = await RunAsync(ffmpeg!, new[]
            {
                "-v", "error", "-i", output,
                "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "NUL",
            });
            Assert.True(decode.ExitCode == 0,
                $"The retained finalized original is not playable: {decode.StandardError}");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task AssertPlayablePrefixAsync(
        string ffmpeg, string output, long committedFrames)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        Assert.True(File.Exists(ffprobe));
        var probe = await RunAsync(ffprobe, new[]
        {
            "-v", "error", "-count_frames", "-show_streams", "-show_packets",
            "-of", "json", output,
        });
        Assert.Equal(0, probe.ExitCode);
        using var json = JsonDocument.Parse(probe.StandardOutput);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(x => x.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(x => x.GetProperty("codec_type").GetString() == "audio");
        Assert.True(long.TryParse(video.GetProperty("nb_read_frames").GetString(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var decodedFrames));
        Assert.Equal(committedFrames, decodedFrames);

        var videoIndex = video.GetProperty("index").GetInt32();
        var audioIndex = audio.GetProperty("index").GetInt32();
        var ends = new Dictionary<int, double>();
        foreach (var packet in json.RootElement.GetProperty("packets").EnumerateArray())
        {
            var index = packet.GetProperty("stream_index").GetInt32();
            if (index != videoIndex && index != audioIndex) continue;
            if (!packet.TryGetProperty("pts_time", out var ptsProperty)
                || !double.TryParse(ptsProperty.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var pts)) continue;
            var duration = packet.TryGetProperty("duration_time", out var durationProperty)
                && double.TryParse(durationProperty.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsedDuration)
                ? parsedDuration
                : 0;
            ends[index] = Math.Max(ends.GetValueOrDefault(index), pts + duration);
        }
        Assert.True(ends.TryGetValue(videoIndex, out var videoEnd));
        Assert.True(ends.TryGetValue(audioIndex, out var audioEnd));
        Assert.True(Math.Abs(videoEnd - audioEnd) <= 0.021,
            $"Playable partial A/V ends differ by {Math.Abs(videoEnd - audioEnd):F6}s.");

        var decode = await RunAsync(ffmpeg, new[]
        {
            "-v", "error", "-i", output,
            "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "NUL",
        });
        Assert.True(decode.ExitCode == 0,
            $"Full partial-file decode failed: {decode.StandardError}");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        string executable, IEnumerable<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await stdout, await stderr);
    }

    private sealed class BurstVideoSource(long start100ns, int itemCount) : IVideoCaptureService
    {
        public bool IsCapturing => true;
        public VideoFormat? CurrentFormat => VideoFormat;
        public CaptureHealth Health { get; } = new()
        {
            State = CaptureHealthState.Running,
            Timestamp100ns = start100ns,
        };
        public event EventHandler<CaptureHealthEventArgs>? HealthChanged
        {
            add { }
            remove { }
        }

        public CaptureSubscription<VideoFrame> Subscribe(int capacity = 4)
            => Subscribe(CaptureSubscriptionOptions.Preview(capacity));

        public CaptureSubscription<VideoFrame> Subscribe(CaptureSubscriptionOptions options)
        {
            var subscription = new CaptureSubscription<VideoFrame>(options, singleWriter: true);
            var bytes = new byte[VideoFormat.BytesPerFrame];
            for (var index = 0; index < itemCount; index++)
            {
                subscription.TryPublish(new VideoFrame
                {
                    Data = bytes,
                    Format = VideoFormat,
                    Timestamp100ns = start100ns
                        + TimeSpan.FromSeconds(index / VideoFormat.FrameRate).Ticks,
                    DeliverySequence = index,
                    FrameNumber = index,
                });
            }
            return subscription;
        }

        public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>(Array.Empty<CaptureDeviceInfo>());
        public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FormatFaultVideoSource(long start100ns) : IVideoCaptureService
    {
        public bool IsCapturing => true;
        public VideoFormat? CurrentFormat => LargeVideoFormat;
        public CaptureHealth Health { get; } = new()
        {
            State = CaptureHealthState.Running,
            Timestamp100ns = start100ns,
        };
        public event EventHandler<CaptureHealthEventArgs>? HealthChanged
        {
            add { }
            remove { }
        }

        public CaptureSubscription<VideoFrame> Subscribe(int capacity = 4)
            => Subscribe(CaptureSubscriptionOptions.Preview(capacity));

        public CaptureSubscription<VideoFrame> Subscribe(CaptureSubscriptionOptions options)
        {
            var subscription = new CaptureSubscription<VideoFrame>(options, singleWriter: true);
            var bytes = new byte[LargeVideoFormat.BytesPerFrame];
            for (var index = 0; index < 4; index++)
            {
                subscription.TryPublish(new VideoFrame
                {
                    Data = bytes,
                    // The first large frame deliberately occupies FFmpeg stdin long
                    // enough for bounded audio lookahead to be submitted. The next
                    // accepted frame then terminates the prefix with a format fault.
                    Format = index == 0
                        ? LargeVideoFormat
                        : LargeVideoFormat with { Width = LargeVideoFormat.Width - 2 },
                    Timestamp100ns = start100ns
                        + TimeSpan.FromSeconds(index / LargeVideoFormat.FrameRate).Ticks,
                    DeliverySequence = index,
                    FrameNumber = index,
                });
            }
            return subscription;
        }

        public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>(Array.Empty<CaptureDeviceInfo>());
        public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BurstAudioSource(long start100ns, int itemCount) : IAudioCaptureService
    {
        private const int SamplesPerPacket = 480;

        public bool IsCapturing => true;
        public AudioFormat? CurrentFormat => AudioFormat;
        public CaptureHealth Health { get; } = new()
        {
            State = CaptureHealthState.Running,
            Timestamp100ns = start100ns,
        };
        public IReadOnlyList<float> PeakLevels => new[] { 0f, 0f };
        public event EventHandler<CaptureHealthEventArgs>? HealthChanged
        {
            add { }
            remove { }
        }

        public CaptureSubscription<AudioBuffer> Subscribe(int capacity = 16)
            => Subscribe(CaptureSubscriptionOptions.Monitor(capacity));

        public CaptureSubscription<AudioBuffer> Subscribe(CaptureSubscriptionOptions options)
        {
            var subscription = new CaptureSubscription<AudioBuffer>(options, singleWriter: true);
            var bytes = new byte[SamplesPerPacket * AudioFormat.Channels * sizeof(short)];
            for (var index = 0; index < itemCount; index++)
            {
                var sampleIndex = index * SamplesPerPacket;
                subscription.TryPublish(new AudioBuffer
                {
                    Data = bytes,
                    Length = bytes.Length,
                    Format = AudioFormat,
                    Timestamp100ns = start100ns
                        + (long)(sampleIndex * 10_000_000.0 / AudioFormat.SampleRate),
                    FirstSampleIndex = sampleIndex,
                    SampleCount = SamplesPerPacket,
                    QpcPosition100ns = start100ns,
                    DevicePosition = sampleIndex,
                    Discontinuity = false,
                    TimestampEstimated = false,
                    Silent = true,
                });
            }
            return subscription;
        }

        public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>(Array.Empty<CaptureDeviceInfo>());
        public Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(
            CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
            => Task.FromResult<CaptureDeviceInfo?>(null);
        public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
