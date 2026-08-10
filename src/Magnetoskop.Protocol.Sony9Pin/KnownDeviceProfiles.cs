using Magnetoskop.Core.Models;

namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// Built-in device profiles for the recorders targeted by the thesis project.
/// Device-type codes and command coverage marked "verify" MUST be confirmed against
/// hardware in Phase 9 (see docs/ARCHITECTURE.md §4.1). Until then the profiles are
/// permissive: an empty SupportedCommands list means "try everything; let NAK
/// 'undefined command' reveal unsupported operations".
/// </summary>
public static class KnownDeviceProfiles
{
    public static VtrDeviceProfile Generic { get; } = VtrDeviceProfile.Generic;

    /// <summary>Sony PVW-2600P. Device type documented in the reference: 2X 40 (X=1 for PAL).</summary>
    public static VtrDeviceProfile Pvw2600P { get; } = new()
    {
        Id = "sony-pvw-2600p",
        DisplayName = "Sony PVW-2600P",
        DeviceTypeCode = "21 40",
        FrameRate = 25,
    };

    /// <summary>Sony DVW-M2000P. Device type confirmed on hardware: <c>B1 04</c>.
    /// Shuttle search tops out at 42× play on this Digital Betacam deck.</summary>
    public static VtrDeviceProfile DvwM2000P { get; } = new()
    {
        Id = "sony-dvw-m2000p",
        DisplayName = "Sony DVW-M2000P",
        DeviceTypeCode = "B1 04",
        FrameRate = 25,
        MaxShuttleRate = 42,
    };

    /// <summary>Sony BVU-950P. Device type code not in the reference — verify on hardware.
    /// VITC support depends on installed options — verify.</summary>
    public static VtrDeviceProfile Bvu950P { get; } = new()
    {
        Id = "sony-bvu-950p",
        DisplayName = "Sony BVU-950P",
        DeviceTypeCode = null, // verify on hardware
        FrameRate = 25,
    };

    /// <summary>Sony UVW-1800P. Device type code not in the reference — verify on hardware.</summary>
    public static VtrDeviceProfile Uvw1800P { get; } = new()
    {
        Id = "sony-uvw-1800p",
        DisplayName = "Sony UVW-1800P",
        DeviceTypeCode = null, // verify on hardware
        FrameRate = 25,
    };

    /// <summary>JVC BR-S622E with Sony-compatible RS-422 control. Command coverage and
    /// timing must be probed on hardware; poll rates start conservative.</summary>
    public static VtrDeviceProfile JvcBrS622E { get; } = new()
    {
        Id = "jvc-br-s622e",
        DisplayName = "JVC BR-S622E",
        DeviceTypeCode = null, // verify on hardware
        FrameRate = 25,
        StatusPollInterval = TimeSpan.FromMilliseconds(300),
        TimecodePollInterval = TimeSpan.FromMilliseconds(120),
        ResponseTimeout = TimeSpan.FromMilliseconds(150),
    };

    public static IReadOnlyList<VtrDeviceProfile> All { get; } = new[]
    {
        Generic, Pvw2600P, DvwM2000P, Bvu950P, Uvw1800P, JvcBrS622E,
    };

    /// <summary>Finds the profile matching a device-type response, else the generic profile.
    /// Matching ignores the X nibble (NTSC/PAL variant) in the first byte.</summary>
    public static VtrDeviceProfile FromDeviceTypeCode(byte byte1, byte byte2)
    {
        foreach (var profile in All)
        {
            if (profile.DeviceTypeCode is null) continue;
            var parts = profile.DeviceTypeCode.Split(' ');
            if (parts.Length != 2) continue;
            var b1 = Convert.ToByte(parts[0], 16);
            var b2 = Convert.ToByte(parts[1], 16);
            if ((b1 & 0xF0) == (byte1 & 0xF0) && b2 == byte2)
            {
                return profile;
            }
        }
        return Generic;
    }
}