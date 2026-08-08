using Magnetoskop.Core.Models;

namespace Magnetoskop.Simulation;

/// <summary>
/// Wire-level behavior definition for a simulated Sony 9-pin deck. Everything not
/// covered by the archived protocol reference is an assumption and is marked as such;
/// docs/HARDWARE_TESTING.md tracks what must be confirmed on the physical machines.
/// </summary>
public sealed record DeckPersonality
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Device Type response bytes (DATA-1, DATA-2). For decks whose real code
    /// is unknown this is a placeholder that intentionally matches no known profile.</summary>
    public required byte DeviceTypeByte1 { get; init; }
    public required byte DeviceTypeByte2 { get; init; }

    /// <summary>When false the deck answers Device Type Request with NAK "undefined command".</summary>
    public bool RespondsToDeviceTypeRequest { get; init; } = true;

    /// <summary>How many status bytes the deck can return (10 documented for DVR-2000;
    /// smaller decks return fewer).</summary>
    public int StatusByteCount { get; init; } = 10;

    /// <summary>Simulated command→response latency (spec allows up to 9 ms).</summary>
    public TimeSpan ResponseLatency { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Whether the deck reads VITC (BVU-950P VITC is an installed option).</summary>
    public bool SupportsVitc { get; init; } = true;

    /// <summary>Transport commands the deck acknowledges; anything else gets
    /// NAK "undefined command". Empty = accept the full documented set.</summary>
    public IReadOnlySet<TransportCommand> SupportedTransportCommands { get; init; }
        = new HashSet<TransportCommand>();

    /// <summary>Swallow the first N commands without any response (exercises the
    /// master's timeout/retry path). Test-only quirk, 0 for the catalog decks.</summary>
    public int DropFirstNCommands { get; init; }

    public bool AcceptsTransportCommand(TransportCommand command)
        => SupportedTransportCommands.Count == 0 || SupportedTransportCommands.Contains(command);
}

/// <summary>
/// The five thesis target decks as simulated wire-level personalities.
/// PVW-2600P (<c>21 40</c>) and DVW-M2000P (<c>B1 04</c>) have confirmed device-type
/// codes; BVU/UVW still use placeholder high byte <c>0x7F</c> so the controller
/// exercises its unknown-device fallback until hardware provides real codes.
/// </summary>
public static class DeckPersonalities
{
    public static DeckPersonality Pvw2600P { get; } = new()
    {
        Id = "sony-pvw-2600p",
        DisplayName = "Sony PVW-2600P (simulated)",
        DeviceTypeByte1 = 0x21, // documented: 2X 40, X=1 for PAL
        DeviceTypeByte2 = 0x40,
        StatusByteCount = 10,
        ResponseLatency = TimeSpan.FromMilliseconds(4),
    };

    public static DeckPersonality DvwM2000P { get; } = new()
    {
        Id = "sony-dvw-m2000p",
        DisplayName = "Sony DVW-M2000P (simulated)",
        DeviceTypeByte1 = 0xB1, // confirmed on hardware: B1 04
        DeviceTypeByte2 = 0x04,
        StatusByteCount = 10,
        ResponseLatency = TimeSpan.FromMilliseconds(3),
    };

    public static DeckPersonality Bvu950P { get; } = new()
    {
        Id = "sony-bvu-950p",
        DisplayName = "Sony BVU-950P (simulated)",
        DeviceTypeByte1 = 0x7F, // placeholder — real code unknown until hardware
        DeviceTypeByte2 = 0x02,
        StatusByteCount = 9,    // assumption: U-matic deck returns fewer status bytes
        ResponseLatency = TimeSpan.FromMilliseconds(6),
        SupportsVitc = false,   // VITC reader is an installed option — verify on hardware
    };

    public static DeckPersonality Uvw1800P { get; } = new()
    {
        Id = "sony-uvw-1800p",
        DisplayName = "Sony UVW-1800P (simulated)",
        DeviceTypeByte1 = 0x7F, // placeholder — real code unknown until hardware
        DeviceTypeByte2 = 0x03,
        StatusByteCount = 10,
        ResponseLatency = TimeSpan.FromMilliseconds(5),
    };

    /// <summary>JVC with a Sony-compatible subset: assumed not to answer Device Type
    /// Request and to reject Record/Preroll/Standby over 9-pin — all "verify on hardware".</summary>
    public static DeckPersonality JvcBrS622E { get; } = new()
    {
        Id = "jvc-br-s622e",
        DisplayName = "JVC BR-S622E (simulated)",
        DeviceTypeByte1 = 0x7F, // never sent (RespondsToDeviceTypeRequest = false)
        DeviceTypeByte2 = 0x04,
        RespondsToDeviceTypeRequest = false,
        StatusByteCount = 9,
        ResponseLatency = TimeSpan.FromMilliseconds(20),
        SupportedTransportCommands = new HashSet<TransportCommand>
        {
            TransportCommand.Play,
            TransportCommand.Stop,
            TransportCommand.FastForward,
            TransportCommand.Rewind,
            TransportCommand.Eject,
        },
    };

    public static IReadOnlyList<DeckPersonality> All { get; } = new[]
    {
        Pvw2600P, DvwM2000P, Bvu950P, Uvw1800P, JvcBrS622E,
    };
}
