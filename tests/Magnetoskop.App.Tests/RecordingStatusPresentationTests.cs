using Magnetoskop.App.ViewModels;
using Magnetoskop.Core.Abstractions;

namespace Magnetoskop.App.Tests;

public sealed class RecordingStatusPresentationTests
{
    [Fact]
    public void IncompleteRecording_IsNotPresentedAsFault()
    {
        var status = new RecordingStatus
        {
            State = RecordingState.Incomplete,
            StopReason = RecordingStopReason.VideoStall,
            Error = "Video input stopped delivering frames.",
        };

        var text = MainViewModel.FormatRecordingStatus(status);

        Assert.StartsWith("Incomplete: VideoStall", text);
        Assert.DoesNotContain("Error:", text);
    }

    [Fact]
    public void SyncTelemetry_ShowsResidualDriftAndAppliedCorrection()
    {
        var status = new RecordingStatus
        {
            State = RecordingState.Recording,
            CurrentAvOffset = TimeSpan.FromMilliseconds(8.4),
            EstimatedDriftPpm = 31.25,
            RequiredCorrectionPpm = -29.75,
            AppliedCorrectionPpm = -20.5,
        };

        var text = MainViewModel.FormatRecordingSyncStatus(status);

        Assert.Contains("residual +8.4 ms", text);
        Assert.Contains("drift +31.3 ppm", text);
        Assert.Contains("correction -20.5 ppm", text);
        Assert.Contains("required -29.8", text);
    }

    [Fact]
    public void IncompleteTelemetry_IsAnExplicitSyncWarning()
    {
        var status = new RecordingStatus
        {
            State = RecordingState.Incomplete,
            StopReason = RecordingStopReason.TimingTelemetryIncomplete,
            SyncTelemetryComplete = false,
        };

        var text = MainViewModel.FormatRecordingSyncStatus(status);

        Assert.StartsWith("A/V SYNC WARNING", text);
        Assert.Contains("timestamp telemetry incomplete", text);
    }
}
