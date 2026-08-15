using System.IO;
using System.Text.Json;
using Magnetoskop.App.Services;
using Magnetoskop.Core.Models;
using Magnetoskop.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.App.Tests;

public sealed class CaptureSessionFormatGateTests
{
    private const string StableDeviceId = "directshow:device-a";
    private static readonly RecordingProfile Profile = RecordingProfile.CreateFfv1Archival();
    private static readonly VideoInputConfiguration Requested = new()
    {
        Standard = VideoInputStandard.Pal,
        ScanMode = VideoScanMode.Tff,
    };

    [Fact]
    public async Task StartRecording_BlocksPendingDriverReadbackEvenWhenAcknowledged()
    {
        var video = CreateVideo(VideoInputFormatStatus.Pending(Requested));
        var coordinator = CreateCoordinator(video);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartRecordingAsync(
                Path.GetTempPath(),
                Profile,
                "Test capture",
                new CaptureSessionStartOptions
                {
                    VideoDeviceStableId = StableDeviceId,
                    VideoFormatMismatchAcknowledged = true,
                    VideoFormatAcknowledgmentKey = "arbitrary-key",
                }));

        Assert.Contains("readback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRecording_RejectsAcknowledgmentFingerprintFromAnotherDevice()
    {
        var status = CreateAcknowledgmentRequiredStatus();
        var video = CreateVideo(status);
        var coordinator = CreateCoordinator(video);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartRecordingAsync(
                Path.GetTempPath(),
                Profile,
                "Test capture",
                new CaptureSessionStartOptions
                {
                    VideoDeviceStableId = StableDeviceId,
                    VideoFormatMismatchAcknowledged = true,
                    VideoFormatAcknowledgmentKey = status.BuildAcknowledgmentKey(
                        "directshow:device-b"),
                }));

        Assert.Contains("acknowledged", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRecording_AcceptsExactAcknowledgedReadbackAndPersistsItsProvenance()
    {
        var status = CreateAcknowledgmentRequiredStatus();
        var video = CreateVideo(status);
        var coordinator = CreateCoordinator(video);
        var directory = Directory.CreateTempSubdirectory("magnetoskop-format-gate").FullName;
        var acknowledgmentKey = status.BuildAcknowledgmentKey(StableDeviceId);

        try
        {
            var outputPath = await coordinator.StartRecordingAsync(
                directory,
                Profile,
                "Test capture",
                new CaptureSessionStartOptions
                {
                    VideoDeviceStableId = StableDeviceId,
                    VideoFormatMismatchAcknowledged = true,
                    VideoFormatAcknowledgmentKey = acknowledgmentKey,
                });
            await coordinator.StopRecordingAsync();

            var metadata = JsonSerializer.Deserialize<RecordingMetadata>(
                await File.ReadAllTextAsync(Path.ChangeExtension(outputPath, ".json")));
            Assert.NotNull(metadata);
            Assert.Equal(StableDeviceId, metadata!.VideoDeviceStableId);
            Assert.True(metadata.VideoFormatAcknowledgmentRequired);
            Assert.True(metadata.VideoFormatMismatchAcknowledged);
            Assert.Equal(acknowledgmentKey, metadata.VideoFormatAcknowledgmentKey);
            Assert.False(metadata.VideoScanReadbackAvailable);
            Assert.NotNull(metadata.ActualVideoInput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FakeConfigurableVideoCaptureService CreateVideo(
        VideoInputFormatStatus status)
        => new()
        {
            IsCapturing = true,
            CurrentFormat = status.Actual,
            InputConfiguration = status.Requested,
            FormatStatus = status,
        };

    private static VideoInputFormatStatus CreateAcknowledgmentRequiredStatus()
        => VideoInputFormatStatus.FromReadback(
            Requested,
            new VideoFormat
            {
                Width = 720,
                Height = 576,
                FrameRate = 25,
                Interlaced = true,
                TopFieldFirst = true,
            },
            scanReadbackAvailable: false);

    private static CaptureSessionCoordinator CreateCoordinator(
        FakeConfigurableVideoCaptureService video)
        => new(
            new FakeVtrController(),
            video,
            new FakeAudioCaptureService { IsCapturing = true },
            new SimulatedRecordingService(
                NullLogger<SimulatedRecordingService>.Instance),
            NullLogger<CaptureSessionCoordinator>.Instance);
}
