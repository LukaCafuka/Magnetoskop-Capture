using System.IO;
using Magnetoskop.App.Services;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.App.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("magnetoskop-settings").FullName;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsService CreateService()
        => new(NullLogger<SettingsService>.Instance, SettingsPath);

    [Fact]
    public void Load_ReturnsDefaultsWhenFileMissing()
    {
        var settings = CreateService().Load();

        Assert.Null(settings.OutputDirectory);
        Assert.False(settings.AutoPlayOnRecord);
        Assert.False(settings.AudioMonitoringEnabled);
        Assert.Equal(100, settings.MonitorVolumePercent);
        Assert.False(settings.DebugLoggingEnabled);
        Assert.False(settings.ShowLogPanel);
        Assert.True(settings.DisableTransportDuringRecording);
        Assert.False(settings.PreviewYadif2xEnabled);
        Assert.False(settings.Ctl24HourWrap);
        Assert.Empty(settings.VideoInputConfigurations);
        Assert.Empty(settings.VideoFormatAcknowledgments);
        Assert.Empty(settings.AvCalibrations);
        Assert.NotNull(settings.Video);
        Assert.Equal(nameof(RecordingCodec.H264), settings.Video!.VideoCodec);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllValues()
    {
        var service = CreateService();
        service.Load();
        service.Current.OutputDirectory = @"C:\captures";
        service.Current.Video = VideoEncodeSettings.FromProfile(RecordingProfile.CreateFfv1Archival());
        service.Current.VideoDeviceId = "video-1";
        service.Current.VideoDeviceName = "Blackmagic WDM Capture";
        service.Current.VideoInputConfigurations["video-1"] = new VideoInputConfiguration
        {
            Standard = VideoInputStandard.Pal,
            ScanMode = VideoScanMode.Bff,
        };
        service.Current.VideoFormatAcknowledgments["video-1"] = new VideoFormatAcknowledgmentSettings
        {
            Fingerprint = "v1|requested-pal-bff|actual-720x576-25",
            AcknowledgedAt = new DateTimeOffset(2026, 8, 15, 11, 30, 0, TimeSpan.Zero),
        };
        service.Current.AudioDeviceId = "audio-2";
        service.Current.AvCalibrations.Add(new AvCalibrationSettings
        {
            VideoDeviceId = "video-1",
            AudioDeviceId = "audio-2",
            AudioOffset100ns = -125_000,
            IsCalibrated = true,
            MeasuredAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        });
        service.Current.AudioManuallySelected = true;
        service.Current.VtrConnectionId = "COM3";
        service.Current.VtrProfileId = "pvw-2600p";
        service.Current.FfmpegPath = @"C:\tools\ffmpeg.exe";
        service.Current.AutoPlayOnRecord = true;
        service.Current.AudioMonitoringEnabled = true;
        service.Current.MonitorVolumePercent = 150;
        service.Current.DebugLoggingEnabled = true;
        service.Current.ShowLogPanel = true;
        service.Current.DisableTransportDuringRecording = false;
        service.Current.PreviewYadif2xEnabled = true;
        service.Current.Ctl24HourWrap = true;
        service.Save();

        var reloaded = CreateService().Load();

        Assert.Equal(@"C:\captures", reloaded.OutputDirectory);
        Assert.NotNull(reloaded.Video);
        Assert.Equal(nameof(RecordingCodec.Ffv1), reloaded.Video!.VideoCodec);
        Assert.Equal("mkv", reloaded.Video.Container);
        Assert.Equal(3, reloaded.Video.Ffv1Level);
        Assert.Equal("video-1", reloaded.VideoDeviceId);
        Assert.Equal("Blackmagic WDM Capture", reloaded.VideoDeviceName);
        Assert.Equal(VideoInputStandard.Pal, reloaded.VideoInputConfigurations["video-1"].Standard);
        Assert.Equal(VideoScanMode.Bff, reloaded.VideoInputConfigurations["video-1"].ScanMode);
        var formatAcknowledgment = reloaded.VideoFormatAcknowledgments["video-1"];
        Assert.Equal("v1|requested-pal-bff|actual-720x576-25", formatAcknowledgment.Fingerprint);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 15, 11, 30, 0, TimeSpan.Zero),
            formatAcknowledgment.AcknowledgedAt);
        Assert.Equal("audio-2", reloaded.AudioDeviceId);
        var calibration = Assert.Single(reloaded.AvCalibrations);
        Assert.Equal("video-1", calibration.VideoDeviceId);
        Assert.Equal("audio-2", calibration.AudioDeviceId);
        Assert.Equal(-125_000, calibration.AudioOffset100ns);
        Assert.True(calibration.IsCalibrated);
        Assert.True(reloaded.AudioManuallySelected);
        Assert.Equal("COM3", reloaded.VtrConnectionId);
        Assert.Equal("pvw-2600p", reloaded.VtrProfileId);
        Assert.Equal(@"C:\tools\ffmpeg.exe", reloaded.FfmpegPath);
        Assert.True(reloaded.AutoPlayOnRecord);
        Assert.True(reloaded.AudioMonitoringEnabled);
        Assert.Equal(150, reloaded.MonitorVolumePercent);
        Assert.True(reloaded.DebugLoggingEnabled);
        Assert.True(reloaded.ShowLogPanel);
        Assert.False(reloaded.DisableTransportDuringRecording);
        Assert.True(reloaded.PreviewYadif2xEnabled);
        Assert.True(reloaded.Ctl24HourWrap);

        var profile = reloaded.Video.ToProfile();
        Assert.Equal(RecordingCodec.Ffv1, profile.VideoCodec);
        Assert.Equal(RecordingAudioCodec.PcmS24Le, profile.AudioCodec);
    }

    [Fact]
    public void VideoEncodeSettings_RoundTripsH265AdvancedOptions()
    {
        var original = RecordingProfile.CreateH265Access() with
        {
            Crf = 26,
            Preset = "slow",
            Tune = "zerolatency",
            VideoProfile = "main10",
            GopSize = 50,
            PixelFormat = "yuv420p",
        };

        var restored = VideoEncodeSettings.FromProfile(original).ToProfile();

        Assert.Equal(RecordingCodec.H265, restored.VideoCodec);
        Assert.Equal(26, restored.Crf);
        Assert.Equal("slow", restored.Preset);
        Assert.Equal("zerolatency", restored.Tune);
        Assert.Equal("main10", restored.VideoProfile);
        Assert.Equal(50, restored.GopSize);
        Assert.Equal("yuv420p", restored.PixelFormat);
        Assert.Equal(RecordingAudioCodec.Aac, restored.AudioCodec);
        Assert.Equal(192, restored.AudioBitrateKbps);
    }

    [Fact]
    public void VideoEncodeSettings_RoundTripsDnxHdProfile()
    {
        var original = RecordingProfile.CreateDnxHdHq() with { DnxHdProfile = "dnxhr_hqx" };
        var restored = VideoEncodeSettings.FromProfile(original).ToProfile();

        Assert.Equal(RecordingCodec.DnxHd, restored.VideoCodec);
        Assert.Equal("dnxhr_hqx", restored.DnxHdProfile);
        Assert.Equal("mov", restored.Container);
        Assert.Equal(RecordingAudioCodec.PcmS16Le, restored.AudioCodec);
    }

    [Fact]
    public void VideoEncodeSettings_RoundTripsAudioCodecAndTuning()
    {
        var original = RecordingProfile.CreateFfv1Archival() with
        {
            AudioCodec = RecordingAudioCodec.Flac,
            FlacCompressionLevel = 8,
            AudioBitrateKbps = 256,
            AudioQuality = 6,
        };

        var restored = VideoEncodeSettings.FromProfile(original).ToProfile();

        Assert.Equal(RecordingAudioCodec.Flac, restored.AudioCodec);
        Assert.Equal(8, restored.FlacCompressionLevel);
        Assert.Equal(256, restored.AudioBitrateKbps);
        Assert.Equal(6, restored.AudioQuality);
    }

    [Fact]
    public void VideoEncodeSettings_CoercesIllegalAudioForContainer()
    {
        var settings = VideoEncodeSettings.FromProfile(RecordingProfile.CreateH264Access());
        settings.AudioCodec = nameof(RecordingAudioCodec.Flac); // not legal in mp4
        settings.Container = "mp4";

        var profile = settings.ToProfile();
        Assert.Equal(RecordingAudioCodec.Aac, profile.AudioCodec);
    }

    [Fact]
    public void Load_RecoversFromCorruptFile()
    {
        File.WriteAllText(SettingsPath, "{ not valid json !!");

        var settings = CreateService().Load();

        Assert.Null(settings.OutputDirectory);
        Assert.NotNull(settings.Video);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
