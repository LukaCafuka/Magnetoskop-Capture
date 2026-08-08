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

    private async Task<FileInfo> RecordAsync(RecordingProfile profile, double seconds = 2.0)
    {
        var output = Path.Combine(_tempDir, $"test_{profile.Id}.{profile.Container}");
        await using var recorder = new FfmpegRecordingService(NullLogger<FfmpegRecordingService>.Instance);

        await recorder.StartAsync(profile, output, _video, _audio);
        Assert.Equal(RecordingState.Recording, recorder.Status.State);

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await recorder.StopAsync();

        Assert.NotEqual(RecordingState.Faulted, recorder.Status.State);
        Assert.True(recorder.Status.VideoFramesWritten > 0, "no frames were written");

        var file = new FileInfo(output);
        Assert.True(file.Exists, $"output file {output} was not created");
        Assert.True(file.Length > 10_000, $"output file is suspiciously small ({file.Length} bytes)");
        return file;
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