using System.IO;
using Magnetoskop.App.Services;
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
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllValues()
    {
        var service = CreateService();
        service.Load();
        service.Current.OutputDirectory = @"C:\captures";
        service.Current.RecordingProfileId = "ffv1-archival";
        service.Current.VideoDeviceId = "video-1";
        service.Current.AudioDeviceId = "audio-2";
        service.Current.AudioManuallySelected = true;
        service.Current.VtrConnectionId = "COM3";
        service.Current.VtrProfileId = "pvw-2600p";
        service.Current.FfmpegPath = @"C:\tools\ffmpeg.exe";
        service.Current.AutoPlayOnRecord = true;
        service.Save();

        var reloaded = CreateService().Load();

        Assert.Equal(@"C:\captures", reloaded.OutputDirectory);
        Assert.Equal("ffv1-archival", reloaded.RecordingProfileId);
        Assert.Equal("video-1", reloaded.VideoDeviceId);
        Assert.Equal("audio-2", reloaded.AudioDeviceId);
        Assert.True(reloaded.AudioManuallySelected);
        Assert.Equal("COM3", reloaded.VtrConnectionId);
        Assert.Equal("pvw-2600p", reloaded.VtrProfileId);
        Assert.Equal(@"C:\tools\ffmpeg.exe", reloaded.FfmpegPath);
        Assert.True(reloaded.AutoPlayOnRecord);
    }

    [Fact]
    public void Load_RecoversFromCorruptFile()
    {
        File.WriteAllText(SettingsPath, "{ not valid json !!");

        var settings = CreateService().Load();

        Assert.Null(settings.OutputDirectory);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
