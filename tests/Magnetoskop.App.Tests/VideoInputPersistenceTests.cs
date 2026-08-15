using Magnetoskop.App.ViewModels;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.Tests;

public sealed class VideoInputPersistenceTests
{
    private static readonly CaptureDeviceInfo Stable = new()
    {
        Id = "@device:pnp:capture-a",
        Name = "USB Capture",
    };

    private static readonly CaptureDeviceInfo SameNameDifferentDevice = new()
    {
        Id = "@device:pnp:capture-b",
        Name = "USB Capture",
    };

    private static readonly CaptureDeviceInfo DefaultDevice = new()
    {
        Id = "@device:pnp:default",
        Name = "Built-in Capture",
        IsDefault = true,
    };

    [Fact]
    public void ResolveVideoDevice_PrefersStableIdOverDuplicateFriendlyName()
    {
        var devices = new[] { SameNameDifferentDevice, Stable };

        var selected = MainViewModel.ResolveVideoDevice(
            devices,
            Stable.Id,
            SameNameDifferentDevice.Name);

        Assert.Same(Stable, selected);
    }

    [Fact]
    public void ResolveVideoDevice_DoesNotReuseFriendlyNameForMissingStableDevice()
    {
        var selected = MainViewModel.ResolveVideoDevice(
            new[] { SameNameDifferentDevice, DefaultDevice },
            "@device:pnp:removed-device",
            SameNameDifferentDevice.Name);

        Assert.Same(DefaultDevice, selected);
    }

    [Fact]
    public void ResolveVideoDevice_UsesFriendlyNameForLegacyNumericSettings()
    {
        var legacyTarget = Stable with { Name = "Legacy Capture" };
        var selected = MainViewModel.ResolveVideoDevice(
            new[] { SameNameDifferentDevice, legacyTarget, DefaultDevice },
            "0",
            legacyTarget.Name);

        Assert.Same(legacyTarget, selected);
    }

    [Fact]
    public void FormatAcknowledgmentKey_ChangesWithRequestedOrActualFormat()
    {
        var palTff = VideoInputConfiguration.SimulatedPalTff;
        var pal = new VideoFormat
        {
            Width = 720,
            Height = 576,
            FrameRate = 25,
            Interlaced = true,
            TopFieldFirst = true,
        };
        var baseStatus = VideoInputFormatStatus.FromReadback(
            palTff,
            pal,
            scanReadbackAvailable: false);

        var original = MainViewModel.BuildVideoFormatAcknowledgmentKey(Stable.Id, baseStatus);
        var changedRequest = MainViewModel.BuildVideoFormatAcknowledgmentKey(
            Stable.Id,
            VideoInputFormatStatus.FromReadback(
                palTff with { ScanMode = VideoScanMode.Bff },
                pal,
                scanReadbackAvailable: false));
        var changedActual = MainViewModel.BuildVideoFormatAcknowledgmentKey(
            Stable.Id,
            VideoInputFormatStatus.FromReadback(
                palTff,
                pal with { FrameRate = 24.98 },
                scanReadbackAvailable: false));

        Assert.NotEqual(original, changedRequest);
        Assert.NotEqual(original, changedActual);
    }
}
