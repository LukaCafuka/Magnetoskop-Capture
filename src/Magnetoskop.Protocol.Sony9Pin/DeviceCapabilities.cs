using System.Collections.Concurrent;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// Learned command support for a connected deck. Starts optimistic (everything
/// allowed by the profile) and records NAK-undefined responses at runtime so the
/// UI can reflect real device capabilities without a hardcoded per-model table.
/// </summary>
public sealed class DeviceCapabilities
{
    private readonly ConcurrentDictionary<TransportCommand, bool> _known = new();
    private readonly ILogger _logger;

    public DeviceCapabilities(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Raised when a command's support status is learned or changes.</summary>
    public event EventHandler<TransportCommand>? CapabilityLearned;

    /// <summary>Null = not yet known; true/false once observed on the wire.</summary>
    public bool? IsSupported(TransportCommand command)
        => _known.TryGetValue(command, out var supported) ? supported : null;

    public void RecordSupported(TransportCommand command)
    {
        if (_known.TryGetValue(command, out var prev) && prev) return;
        _known[command] = true;
        _logger.LogInformation("Device capability learned: {Command} is supported", command);
        CapabilityLearned?.Invoke(this, command);
    }

    public void RecordUnsupported(TransportCommand command)
    {
        if (_known.TryGetValue(command, out var prev) && !prev) return;
        _known[command] = false;
        _logger.LogWarning("Device capability learned: {Command} is NOT supported by this deck", command);
        CapabilityLearned?.Invoke(this, command);
    }

    /// <summary>All commands observed so far with their support state.</summary>
    public IReadOnlyDictionary<TransportCommand, bool> Snapshot()
        => new Dictionary<TransportCommand, bool>(_known);

    public void Reset() => _known.Clear();
}