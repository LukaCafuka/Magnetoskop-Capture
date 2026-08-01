using System.IO;
using System.Text.Json;
using Magnetoskop.App.Services;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.App.Tests;

public class CaptureSessionCoordinatorTests
{
    private static readonly RecordingProfile Ffv1Profile =
        RecordingProfile.Defaults.First(p => p.Id == "ffv1-archival");

    private readonly FakeVtrController _vtr = new();
    private readonly FakeVideoCaptureService _video = new();
    private readonly FakeAudioCaptureService _audio = new();
    private readonly SimulatedRecordingService _recorder =
        new(NullLogger<SimulatedRecordingService>.Instance);

    private CaptureSessionCoordinator CreateCoordinator() => new(
        _vtr, _video, _audio, _recorder,
        NullLogger<CaptureSessionCoordinator>.Instance);

    /// <summary>The ffmpeg warning depends on the machine; ignore it in assertions.</summary>
    private static IReadOnlyList<string> WithoutFfmpeg(IReadOnlyList<string> warnings)
        => warnings.Where(w => !w.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)).ToList();

    // ---- Preflight warnings ------------------------------------------------

    [Fact]
    public void Preflight_WarnsWhenNothingIsReady()
    {
        var warnings = WithoutFfmpeg(CreateCoordinator().PreflightWarnings());

        Assert.Contains(warnings, w => w.Contains("Video capture is not running"));
        Assert.Contains(warnings, w => w.Contains("no audio track"));
        Assert.Contains(warnings, w => w.Contains("No recorder connected"));
    }

    [Fact]
    public void Preflight_CleanWhenEverythingIsReady()
    {
        _video.IsCapturing = true;
        _audio.IsCapturing = true;
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { Transport = TransportState.Playing };

        var warnings = WithoutFfmpeg(CreateCoordinator().PreflightWarnings());

        Assert.Empty(warnings);
    }

    [Fact]
    public void Preflight_WarnsOnTapeOutAndNotPlaying()
    {
        _video.IsCapturing = true;
        _audio.IsCapturing = true;
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { Transport = TransportState.Stopped, TapeOut = true };

        var warnings = WithoutFfmpeg(CreateCoordinator().PreflightWarnings());

        Assert.Contains(warnings, w => w.Contains("no tape loaded"));
        Assert.Contains(warnings, w => w.Contains("not Playing"));
    }

    [Fact]
    public void Preflight_AutoPlaySuppressesNotPlayingWarning()
    {
        _video.IsCapturing = true;
        _audio.IsCapturing = true;
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { Transport = TransportState.Stopped };

        var warnings = WithoutFfmpeg(CreateCoordinator().PreflightWarnings(autoPlayEnabled: true));

        Assert.DoesNotContain(warnings, w => w.Contains("not Playing"));
    }

    // ---- Output path naming -------------------------------------------------

    [Fact]
    public void BuildOutputPath_IncludesTimecodeWhenAvailable()
    {
        _vtr.IsConnected = true;
        _vtr.CurrentTime = new TimeInformation
        {
            Ltc = new Timecode(1, 2, 3, 4),
            PrimarySource = TimecodeSource.Ltc,
        };

        var path = CreateCoordinator().BuildOutputPath(@"C:\out", Ffv1Profile);

        Assert.Contains("_TC01-02-03-04", path);
        Assert.EndsWith(".mkv", path);
        Assert.StartsWith(@"C:\out", path);
    }

    [Fact]
    public void BuildOutputPath_OmitsTimecodeWithoutVtr()
    {
        var path = CreateCoordinator().BuildOutputPath(@"C:\out", Ffv1Profile);

        Assert.DoesNotContain("_TC", path);
        Assert.EndsWith(".mkv", path);
    }

    [Fact]
    public void BuildOutputPath_PrefersVitcWhenItIsThePrimarySource()
    {
        _vtr.IsConnected = true;
        _vtr.CurrentTime = new TimeInformation
        {
            Ltc = new Timecode(9, 9, 9, 9),
            Vitc = new Timecode(1, 0, 0, 0),
            PrimarySource = TimecodeSource.Vitc,
        };

        var path = CreateCoordinator().BuildOutputPath(@"C:\out", Ffv1Profile);

        Assert.Contains("_TC01-00-00-00", path);
    }

    // ---- Auto-play -----------------------------------------------------------

    [Fact]
    public async Task EnsurePlaying_ReturnsFalseWhenNotConnected()
    {
        Assert.False(await CreateCoordinator().EnsurePlayingAsync());
        Assert.Empty(_vtr.SentCommands);
    }

    [Fact]
    public async Task EnsurePlaying_ReturnsFalseOnTapeOut()
    {
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { TapeOut = true };

        Assert.False(await CreateCoordinator().EnsurePlayingAsync());
        Assert.Empty(_vtr.SentCommands);
    }

    [Fact]
    public async Task EnsurePlaying_NoCommandWhenAlreadyPlaying()
    {
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { Transport = TransportState.Playing, ServoLock = true };

        Assert.True(await CreateCoordinator().EnsurePlayingAsync());
        Assert.Empty(_vtr.SentCommands);
    }

    [Fact]
    public async Task EnsurePlaying_SendsPlayAndWaitsForServoLock()
    {
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { Transport = TransportState.Stopped };
        _vtr.OnTransportCommand = cmd =>
        {
            if (cmd == TransportCommand.Play)
            {
                _vtr.CurrentStatus = new VtrStatus
                {
                    Transport = TransportState.Playing,
                    ServoLock = true,
                };
            }
        };

        Assert.True(await CreateCoordinator().EnsurePlayingAsync());
        Assert.Equal(new[] { TransportCommand.Play }, _vtr.SentCommands);
    }

    [Fact]
    public async Task EnsurePlaying_TimesOutWhenDeckNeverLocks()
    {
        _vtr.IsConnected = true;
        _vtr.CurrentStatus = new VtrStatus { Transport = TransportState.Stopped };

        var coordinator = CreateCoordinator();
        coordinator.AutoPlayTimeout = TimeSpan.FromMilliseconds(200);

        Assert.False(await coordinator.EnsurePlayingAsync());
        Assert.Equal(new[] { TransportCommand.Play }, _vtr.SentCommands);
    }

    // ---- Recording session + sidecar metadata ---------------------------------

    [Fact]
    public async Task RecordingSession_WritesSidecarMetadata()
    {
        _video.IsCapturing = true;
        _audio.IsCapturing = true;
        _vtr.IsConnected = true;
        _vtr.DeviceDescription = "Test Deck";
        _vtr.CurrentTime = new TimeInformation
        {
            Ctl = new Timecode(0, 0, 10, 0),
            Ltc = new Timecode(1, 2, 3, 4),
            LtcUserBits = new UserBits(0x20, 0x26, 0x01, 0x01),
            PrimarySource = TimecodeSource.Ltc,
        };

        var dir = Directory.CreateTempSubdirectory("magnetoskop-test").FullName;
        try
        {
            var coordinator = CreateCoordinator();
            var outputPath = await coordinator.StartRecordingAsync(
                dir, Ffv1Profile, "Test Capture Device");
            await coordinator.StopRecordingAsync();

            var sidecarPath = Path.ChangeExtension(outputPath, ".json");
            Assert.True(File.Exists(sidecarPath), $"sidecar not found at {sidecarPath}");

            var metadata = JsonSerializer.Deserialize<RecordingMetadata>(
                await File.ReadAllTextAsync(sidecarPath));
            Assert.NotNull(metadata);
            Assert.Equal("Test Capture Device", metadata!.SourceDevice);
            Assert.Equal("Test Deck", metadata.VtrDevice);
            Assert.Equal(Ffv1Profile.DisplayName, metadata.RecordingProfile);
            Assert.Equal("01:02:03:04", metadata.StartTimecode);
            Assert.Equal("00:00:10:00", metadata.StartCtl);
            Assert.Equal("Ltc", metadata.TimecodeSource);
            Assert.NotNull(metadata.EndedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RecordingSession_NoTimecodeMetadataWithoutVtr()
    {
        _video.IsCapturing = true;

        var dir = Directory.CreateTempSubdirectory("magnetoskop-test").FullName;
        try
        {
            var coordinator = CreateCoordinator();
            var outputPath = await coordinator.StartRecordingAsync(dir, Ffv1Profile, null);
            await coordinator.StopRecordingAsync();

            var metadata = JsonSerializer.Deserialize<RecordingMetadata>(
                await File.ReadAllTextAsync(Path.ChangeExtension(outputPath, ".json")));
            Assert.NotNull(metadata);
            Assert.Null(metadata!.StartTimecode);
            Assert.Null(metadata.VtrDevice);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
