using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Tests;

public sealed class VideoInputConfigurationTests
{
    [Fact]
    public void UnspecifiedConfiguration_IsNotExplicit()
    {
        var configuration = new VideoInputConfiguration();

        Assert.False(configuration.IsExplicit);
        Assert.Null(configuration.RequestedWidth);
        Assert.Null(configuration.RequestedFrameRate);
    }

    [Fact]
    public void PalBff_ResolvesConventionalFormatAndFieldOrder()
    {
        var configuration = new VideoInputConfiguration
        {
            Standard = VideoInputStandard.Pal,
            ScanMode = VideoScanMode.Bff,
        };

        Assert.True(configuration.IsExplicit);
        Assert.Equal(720, configuration.RequestedWidth);
        Assert.Equal(576, configuration.RequestedHeight);
        Assert.Equal(25, configuration.RequestedFrameRate);
        Assert.True(configuration.Interlaced);
        Assert.False(configuration.TopFieldFirst);
    }

    [Fact]
    public void Ntsc_UsesExactFractionalRate()
    {
        var configuration = new VideoInputConfiguration
        {
            Standard = VideoInputStandard.Ntsc,
            ScanMode = VideoScanMode.Progressive,
        };

        Assert.Equal(30_000d / 1_001d, configuration.RequestedFrameRate);
        Assert.False(configuration.Interlaced);
    }

    [Fact]
    public void CustomRequiresPositiveFiniteValuesAndScanMode()
    {
        var invalid = new VideoInputConfiguration
        {
            Standard = VideoInputStandard.Custom,
            ScanMode = VideoScanMode.Tff,
            CustomWidth = 0,
        };
        var valid = invalid with { CustomWidth = 1920, CustomHeight = 1080, CustomFrameRate = 50 };

        Assert.False(invalid.IsExplicit);
        Assert.True(valid.IsExplicit);
        Assert.Equal(1920, valid.RequestedWidth);
        Assert.Equal(50, valid.RequestedFrameRate);
    }

    [Fact]
    public void MatchingDriverReadback_StillRequiresAcknowledgmentWhenScanCannotBeMeasured()
    {
        var requested = VideoInputConfiguration.SimulatedPalTff;
        var actual = new VideoFormat
        {
            Width = 720,
            Height = 576,
            FrameRate = 25,
            Interlaced = true,
            TopFieldFirst = true,
        };

        var status = VideoInputFormatStatus.FromReadback(requested, actual);

        Assert.True(status.MatchesRequested);
        Assert.False(status.ScanReadbackAvailable);
        Assert.True(status.AcknowledgmentRequired);
        Assert.NotNull(status.Message);
    }

    [Fact]
    public void FullyMeasuredMatchingFormat_DoesNotRequireAcknowledgment()
    {
        var requested = VideoInputConfiguration.SimulatedPalTff;
        var actual = new VideoFormat
        {
            Width = 720,
            Height = 576,
            FrameRate = 25,
            Interlaced = true,
            TopFieldFirst = true,
        };

        var status = VideoInputFormatStatus.FromReadback(
            requested,
            actual,
            scanReadbackAvailable: true);

        Assert.True(status.MatchesRequested);
        Assert.True(status.ScanReadbackAvailable);
        Assert.False(status.AcknowledgmentRequired);
        Assert.Null(status.Message);
    }
}
