using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Recording;
using Magnetoskop.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Recording.Tests;

/// <summary>
/// End-to-end recording tests: simulated capture sources → FfmpegRecordingService →
/// real ffmpeg → output file. Skipped automatically when ffmpeg.exe is not available.
/// </summary>
public class FfmpegRecordingIntegrationTests : IAsyncLifetime
{
    private static readonly string? FfmpegPath = FfmpegLocator.Find();

    private SimulatedVideoCaptureService _video = null!;
    private SimulatedAudioCaptureService _audio = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"magnetoskop_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _video = new SimulatedVideoCaptureService(NullLogger<SimulatedVideoCaptureService>.Instance);
        _audio = new SimulatedAudioCaptureService(NullLogger<SimulatedAudioCaptureService>.Instance);

        var videoDevices = await _video.EnumerateDevicesAsync();
        var audioDevices = await _audio.EnumerateDevicesAsync();
        await _video.StartAsync(videoDevices[0]);
        await _audio.StartAsync(audioDevices[0]);
    }

    public async Task DisposeAsync()
    {
        await _video.DisposeAsync();
        await _audio.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private async Task<(FileInfo File, RecordingResult Result)> RecordAsync(
        RecordingProfile profile,
        double seconds = 2.0)
    {
        var output = Path.Combine(_tempDir, $"test_{profile.Id}.{profile.Container}");
        await using var recorder = new FfmpegRecordingService(NullLogger<FfmpegRecordingService>.Instance);

        await recorder.StartAsync(profile, output, _video, _audio);
        Assert.Equal(RecordingState.Recording, recorder.Status.State);

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var result = await recorder.StopAsync(RecordingStopReason.UserRequested);

        Assert.True(result.Outcome != RecordingOutcome.Faulted,
            $"{result.StopReason}: {result.Error}; " +
            $"video={result.Metrics.VideoFramesWritten}, audio={result.Metrics.AudioSamplesWritten}/" +
            $"{result.Metrics.AudioSamplesSubmittedToEncoder}, " +
            $"vq={result.Metrics.VideoQueueAccepted}/{result.Metrics.VideoQueueConsumed}/" +
            $"{result.Metrics.VideoQueueRejectedNew}");
        Assert.True(recorder.Status.VideoFramesWritten > 0, "no frames were written");

        var file = new FileInfo(output);
        Assert.True(file.Exists, $"output file {output} was not created");
        Assert.True(file.Length > 10_000, $"output file is suspiciously small ({file.Length} bytes)");
        await AssertProbeAndDecodeAsync(output, result, _video.CurrentFormat!);
        return (file, result);
    }

    private static async Task AssertProbeAndDecodeAsync(
        string output,
        RecordingResult result,
        VideoFormat videoFormat)
    {
        var ffprobe = FindSiblingTool("ffprobe.exe");
        Assert.NotNull(ffprobe);

        var probe = await RunToolAsync(ffprobe!, new[]
        {
            "-v", "error", "-count_frames", "-show_streams", "-show_format",
            "-show_packets", "-of", "json", output,
        });
        Assert.True(probe.ExitCode == 0, probe.StandardError);

        using var document = JsonDocument.Parse(probe.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream =>
            stream.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(stream =>
            stream.GetProperty("codec_type").GetString() == "audio");
        var videoIndex = video.GetProperty("index").GetInt32();
        var audioIndex = audio.GetProperty("index").GetInt32();

        Assert.True(long.TryParse(video.GetProperty("nb_read_frames").GetString(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var decodedFrames));
        Assert.Equal(result.Metrics.VideoFramesWritten, decodedFrames);
        Assert.Equal(videoFormat.FrameRate,
            ParseRate(video.GetProperty("avg_frame_rate").GetString()!), 3);
        Assert.Equal(48000, int.Parse(audio.GetProperty("sample_rate").GetString()!,
            CultureInfo.InvariantCulture));

        var packetEnds = new Dictionary<int, double>();
        foreach (var packet in document.RootElement.GetProperty("packets").EnumerateArray())
        {
            var index = packet.GetProperty("stream_index").GetInt32();
            if (index != videoIndex && index != audioIndex) continue;
            if (!TryDouble(packet, "pts_time", out var pts)) continue;
            _ = TryDouble(packet, "duration_time", out var duration);
            packetEnds[index] = Math.Max(packetEnds.GetValueOrDefault(index), pts + duration);
        }

        Assert.True(packetEnds.TryGetValue(videoIndex, out var videoEnd));
        Assert.True(packetEnds.TryGetValue(audioIndex, out var audioEnd));
        var expectedVideoDuration = decodedFrames / videoFormat.FrameRate;
        var oneFieldSeconds = 1.0 /
            (videoFormat.FrameRate * (videoFormat.Interlaced ? 2.0 : 1.0));
        var videoDurationError = Math.Abs(videoEnd - expectedVideoDuration);
        Assert.True(videoDurationError <= oneFieldSeconds + 0.001,
            $"Video end {videoEnd:F6}s differs from {decodedFrames} frame(s) / " +
            $"{videoFormat.FrameRate:F6} fps = {expectedVideoDuration:F6}s by " +
            $"{videoDurationError:F6}s.");
        var avEndError = Math.Abs(audioEnd - videoEnd);
        Assert.True(avEndError <= oneFieldSeconds + 0.001,
            $"A/V packet ends differ by {avEndError:F6}s " +
            $"(video={videoEnd:F6}s, audio={audioEnd:F6}s, one field={oneFieldSeconds:F6}s).");

        var decode = await RunToolAsync(FfmpegPath!, new[]
        {
            "-v", "error", "-i", output, "-map", "0:v:0", "-map", "0:a:0",
            "-f", "null", "NUL",
        });
        Assert.True(decode.ExitCode == 0,
            $"Full decode failed ({decode.ExitCode}): {decode.StandardError}");
    }

    private static string? FindSiblingTool(string name)
    {
        if (FfmpegPath is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(FfmpegPath)!, name);
            if (File.Exists(sibling)) return sibling;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), name))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunToolAsync(
        string executable,
        IEnumerable<string> arguments)
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

    private static bool TryDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && double.TryParse(property.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
    }

    private static double ParseRate(string rate)
    {
        var parts = rate.Split('/');
        return parts.Length == 2
            ? double.Parse(parts[0], CultureInfo.InvariantCulture)
                / double.Parse(parts[1], CultureInfo.InvariantCulture)
            : double.Parse(rate, CultureInfo.InvariantCulture);
    }

    [SkippableFact]
    public async Task Ffv1Mkv_RecordsAndFinalizes()
    {
        Skip.If(FfmpegPath is null, "ffmpeg.exe not found on this machine");
        await RecordAsync(RecordingProfile.CreateFfv1Archival());
    }

    [SkippableFact]
    public async Task H264Mp4_RecordsAndFinalizes()
    {
        Skip.If(FfmpegPath is null, "ffmpeg.exe not found on this machine");
        await RecordAsync(RecordingProfile.CreateH264Access());
    }

    [SkippableFact]
    public async Task ProResMov_RecordsAndFinalizes()
    {
        Skip.If(FfmpegPath is null, "ffmpeg.exe not found on this machine");
        await RecordAsync(RecordingProfile.CreateProResHq());
    }

    [SkippableFact]
    public async Task DnxHdMov_RecordsAndFinalizes()
    {
        Skip.If(FfmpegPath is null, "ffmpeg.exe not found on this machine");
        await RecordAsync(RecordingProfile.CreateDnxHdHq());
    }

    [SkippableFact]
    public async Task StartTwice_Throws()
    {
        Skip.If(FfmpegPath is null, "ffmpeg.exe not found on this machine");

        var profile = RecordingProfile.CreateFfv1Archival();
        var output = Path.Combine(_tempDir, "twice.mkv");
        await using var recorder = new FfmpegRecordingService(NullLogger<FfmpegRecordingService>.Instance);
        await recorder.StartAsync(profile, output, _video, _audio);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.StartAsync(profile, output, _video, _audio));

        await recorder.StopAsync();
    }

    [Fact]
    public async Task Start_WithoutRunningVideoCapture_Throws()
    {
        var stoppedVideo = new SimulatedVideoCaptureService(NullLogger<SimulatedVideoCaptureService>.Instance);
        await using var recorder = new FfmpegRecordingService(NullLogger<FfmpegRecordingService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(
            RecordingProfile.CreateFfv1Archival(),
            Path.Combine(_tempDir, "x.mkv"),
            stoppedVideo, _audio));
    }
}
