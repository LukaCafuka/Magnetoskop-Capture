namespace Magnetoskop.Protocol.Sony9Pin.Tests;

public class KnownDeviceProfilesTests
{
    [Fact]
    public void All_ContainsGenericAndFiveTargetDecks()
    {
        Assert.Equal(6, KnownDeviceProfiles.All.Count);
        Assert.Contains(KnownDeviceProfiles.All, p => p.Id == "generic-sony9pin");
        Assert.Contains(KnownDeviceProfiles.All, p => p.Id == "sony-pvw-2600p");
        Assert.Contains(KnownDeviceProfiles.All, p => p.Id == "sony-dvw-m2000p");
        Assert.Contains(KnownDeviceProfiles.All, p => p.Id == "sony-bvu-950p");
        Assert.Contains(KnownDeviceProfiles.All, p => p.Id == "sony-uvw-1800p");
        Assert.Contains(KnownDeviceProfiles.All, p => p.Id == "jvc-br-s622e");
    }

    [Fact]
    public void FromDeviceTypeCode_MatchesPvw2600IgnoringVariantNibble()
    {
        // 20 40 = NTSC variant, 21 40 = PAL variant; both map to the PVW-2600 profile.
        Assert.Equal("sony-pvw-2600p", KnownDeviceProfiles.FromDeviceTypeCode(0x20, 0x40).Id);
        Assert.Equal("sony-pvw-2600p", KnownDeviceProfiles.FromDeviceTypeCode(0x21, 0x40).Id);
    }

    [Fact]
    public void FromDeviceTypeCode_FallsBackToGeneric()
    {
        Assert.Equal("generic-sony9pin", KnownDeviceProfiles.FromDeviceTypeCode(0x3F, 0xFF).Id);
    }

    [Fact]
    public void PermissiveProfiles_AllowAllCommands()
    {
        foreach (var profile in KnownDeviceProfiles.All)
        {
            Assert.True(profile.IsCommandSupported(Core.Models.TransportCommand.Play));
            Assert.True(profile.IsCommandSupported(Core.Models.TransportCommand.Eject));
        }
    }
}