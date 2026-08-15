using Magnetoskop.Capture.Video;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.Tests;

public sealed class DirectShowDeviceResolverTests
{
    [Fact]
    public void StablePathFollowsDeviceAcrossReorderedIndices()
    {
        var devices = new[]
        {
            Device("path-c", "Capture C"),
            Device("path-a", "Capture A"),
            Device("path-b", "Capture B"),
        };

        Assert.Equal(2, DirectShowDeviceEnumerator.ResolveIndex("path-b", devices));
    }

    [Fact]
    public void DuplicateFriendlyNamesAreResolvedByStablePath()
    {
        var devices = new[]
        {
            Device("usb#vid_1111&instance_1", "USB Video"),
            Device("usb#vid_1111&instance_2", "USB Video"),
        };

        Assert.Equal(1, DirectShowDeviceEnumerator.ResolveIndex(
            "USB#VID_1111&INSTANCE_2", devices));
    }

    [Fact]
    public void RemovedStableDeviceDoesNotFallBackToSameFriendlyName()
    {
        var devices = new[] { Device("replacement-path", "USB Video") };

        Assert.Null(DirectShowDeviceEnumerator.ResolveIndex("removed-path", devices));
    }

    [Fact]
    public void LegacyNumericSettingRemainsTransientIndexFallback()
    {
        var devices = new[] { Device("path-a", "A"), Device("path-b", "B") };

        Assert.Equal(1, DirectShowDeviceEnumerator.ResolveIndex("1", devices));
        Assert.Null(DirectShowDeviceEnumerator.ResolveIndex("2", devices));
    }

    private static CaptureDeviceInfo Device(string id, string name) => new()
    {
        Id = id,
        Name = name,
    };
}
