using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Abstractions;

/// <summary>Thrown when the active device (or its profile) does not support a command.</summary>
public sealed class UnsupportedCommandException : InvalidOperationException
{
    public UnsupportedCommandException(string message) : base(message) { }
}

/// <summary>Thrown when the recorder fails to respond or reports a communication error.</summary>
public sealed class VtrCommunicationException : IOException
{
    public VtrCommunicationException(string message) : base(message) { }
    public VtrCommunicationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Abstraction over a videotape recorder. Implemented by the Sony 9-pin protocol
/// controller (real hardware) and by the simulated VTR.
/// </summary>
public interface IVtrController : IAsyncDisposable
{
    /// <summary>Human-readable description of the connected device.</summary>
    string DeviceDescription { get; }

    bool IsConnected { get; }

    /// <summary>Raised whenever a fresh status snapshot is available (from polling).</summary>
    event EventHandler<VtrStatus>? StatusChanged;

    /// <summary>Raised whenever fresh time information is available (from polling).</summary>
    event EventHandler<TimeInformation>? TimeChanged;

    /// <summary>Opens the connection and starts status/timecode polling.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops polling and closes the connection.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a transport command and awaits acknowledgement.</summary>
    /// <exception cref="UnsupportedCommandException">The device does not support the command.</exception>
    /// <exception cref="VtrCommunicationException">The device did not acknowledge the command.</exception>
    Task SendTransportCommandAsync(TransportCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a Jog or Shuttle command with a Sony 9-pin speed byte
    /// (<c>TapeSpeed = 10^((N/32)-2)</c> × play; <c>N=0</c> = still).
    /// </summary>
    Task SendVariableSpeedAsync(
        VariableSpeedMode mode,
        bool forward,
        byte speed,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the latest known status (does not force a poll).</summary>
    VtrStatus CurrentStatus { get; }

    /// <summary>Reads the latest known time information (does not force a poll).</summary>
    TimeInformation CurrentTime { get; }
}