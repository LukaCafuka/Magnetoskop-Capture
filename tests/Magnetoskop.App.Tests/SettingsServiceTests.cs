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
        Assert.False(settings.DebugLoggingEnabled);
        Assert.False(settings.ShowLogPanel);
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
        service.Current.AudioDeviceId = "audio-2";
        service.Current.AudioManuallySelected = true;
        service.Current.VtrConnectionId = "COM3";
        service.Current.VtrProfileId = "pvw-2600p";
        service.Current.FfmpegPath = @"C:\tools\ffmpeg.exe";
        service.Current.AutoPlayOnRecord = true;
        service.Current.AudioMonitoringEnabled = true;
        service.Current.DebugLoggingEnabled = true;
        service.Current.ShowLogPanel = true;
        service.Save();

        var reloaded = CreateService().Load();

        Assert.Equal(@"C:\captures", reloaded.OutputDirectory);
        Assert.NotNull(reloaded.Video);
        Assert.Equal(nameof(RecordingCodec.Ffv1), reloaded.Video!.VideoCodec);
        Assert.Equal("mkv", reloaded.Video.Container);
        Assert.Equal(3, reloaded.Video.Ffv1Level);
        Assert.Equal("video-1", reloaded.VideoDeviceId);
        Assert.Equal("audio-2", reloaded.AudioDeviceId);
        Assert.True(reloaded.AudioManuallySelected);
        Assert.Equal("COM3", reloaded.VtrConnectionId);
        Assert.Equal("pvw-2600p", reloaded.VtrProfileId);
        Assert.Equal(@"C:\tools\ffmpeg.exe", reloaded.FfmpegPath);
        Assert.True(reloaded.AutoPlayOnRecord);
        Assert.True(reloaded.AudioMonitoringEnabled);
        Assert.True(reloaded.DebugLoggingEnabled);
        Assert.True(reloaded.ShowLogPanel);

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
