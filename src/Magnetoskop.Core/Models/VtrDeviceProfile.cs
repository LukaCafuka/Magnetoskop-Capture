namespace Magnetoskop.Core.Models;

/// <summary>
/// Describes a specific recorder model (or a generic fallback):
/// which commands it supports, its polling cadence, and serial settings overrides.
/// Values marked "verify on hardware" in ARCHITECTURE.md must be confirmed per model.
/// </summary>
public sealed record VtrDeviceProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Device-type bytes returned by the 00 11 Device Type Request, when known (e.g., "20 40").</summary>
    public string? DeviceTypeCode { get; init; }

    /// <summary>Commands this deck is known to support. Empty = assume the full generic set.</summary>
    public IReadOnlyCollection<TransportCommand> SupportedCommands { get; init; } = Array.Empty<TransportCommand>();

    /// <summary>Status poll interval.</summary>
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Timecode poll interval.</summary>
    public TimeSpan TimecodePollInterval { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Per-command response timeout. Protocol requires slave response within 9 ms;
    /// this is deliberately generous to allow for USB adapter latency.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Number of retries for a command that timed out or received a transmission-error NAK.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Nominal frame rate of the deck's video standard (25 for PAL models).</summary>
    public int FrameRate { get; init; } = 25;

    /// <summary>
    /// Maximum shuttle search speed as a multiple of play (wheel full deflection / JKL top step).
    /// Default ~50× matches the DVR-2000 reference; Digital Betacam DVW-M2000P is 42×.
    /// </summary>
    public double MaxShuttleRate { get; init; } = 50.0;

    public bool SupportsVitc { get; init; } = true;
    public bool SupportsLtc { get; init; } = true;

    public bool IsCommandSupported(TransportCommand command)
        => SupportedCommands.Count == 0 || SupportedCommands.Contains(command);

    public static VtrDeviceProfile Generic { get; } = new()
    {
        Id = "generic-sony9pin",
        DisplayName = "Generic Sony 9-pin VTR",
    };
}