using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Protocol.Sony9Pin;
using Magnetoskop.Serial;
using Magnetoskop.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>Describes a selectable VTR connection target.</summary>
public sealed record VtrConnectionOption(string Id, string DisplayName)
{
    public const string SimulatorId = "simulator";
    public override string ToString() => DisplayName;
}

/// <summary>
/// Runtime-switchable VTR source: either the built-in simulator or a
/// Sony 9-pin controller on a chosen COM port with a chosen device profile.
/// Presents a stable IVtrController facade to the view model.
/// </summary>
public sealed class VtrConnectionService : IVtrController
{
    private readonly IServiceProvider _services;
    private readonly ISerialPortEnumerator _portEnumerator;
    private readonly ILogger<VtrConnectionService> _logger;

    private IVtrController _current;
    private bool _currentIsSimulator = true;

    public VtrConnectionService(
        IServiceProvider services,
        ISerialPortEnumerator portEnumerator,
        SimulatedVtr simulator,
        ILogger<VtrConnectionService> logger)
    {
        _services = services;
        _portEnumerator = portEnumerator;
        _logger = logger;
        _current = simulator;
        Attach(_current);
    }

    public IReadOnlyList<VtrConnectionOption> GetConnectionOptions()
    {
        var options = new List<VtrConnectionOption>
        {
            new(VtrConnectionOption.SimulatorId, "Simulator (no hardware)"),
        };
        foreach (var port in _portEnumerator.GetPortNames())
        {
            options.Add(new VtrConnectionOption(port, $"Sony 9-pin on {port}"));
        }
        return options;
    }

    public IReadOnlyList<VtrDeviceProfile> GetDeviceProfiles() => KnownDeviceProfiles.All;

    /// <summary>Switches the active connection target. Disconnects the previous one.</summary>
    public async Task SwitchAsync(VtrConnectionOption option, VtrDeviceProfile profile, CancellationToken ct = default)
    {
        // Tear down the current connection.
        Detach(_current);
        await _current.DisconnectAsync(ct);
        if (!_currentIsSimulator)
        {
            await _current.DisposeAsync();
        }

        if (option.Id == VtrConnectionOption.SimulatorId)
        {
            _current = _services.GetRequiredService<SimulatedVtr>();
            _currentIsSimulator = true;
        }
        else
        {
            var transport = new SerialPortTransport(
                _services.GetRequiredService<ILogger<SerialPortTransport>>());
            var settings = new SerialSettings { PortName = option.Id };
            _current = new Sony9PinController(transport, profile, settings,
                _services.GetRequiredService<ILogger<Sony9PinController>>());
            _currentIsSimulator = false;
        }

        Attach(_current);
        await _current.ConnectAsync(ct);
        _logger.LogInformation("VTR source switched to {Target} ({Profile})",
            option.DisplayName, profile.DisplayName);
    }

    private void Attach(IVtrController controller)
    {
        controller.StatusChanged += ForwardStatus;
        controller.TimeChanged += ForwardTime;
        if (controller is Sony9PinController sony)
        {
            sony.Capabilities.CapabilityLearned += ForwardCapability;
        }
    }

    private void Detach(IVtrController controller)
    {
        controller.StatusChanged -= ForwardStatus;
        controller.TimeChanged -= ForwardTime;
        if (controller is Sony9PinController sony)
        {
            sony.Capabilities.CapabilityLearned -= ForwardCapability;
        }
    }

    private void ForwardStatus(object? sender, VtrStatus status) => StatusChanged?.Invoke(this, status);
    private void ForwardTime(object? sender, TimeInformation time) => TimeChanged?.Invoke(this, time);
    private void ForwardCapability(object? sender, TransportCommand command)
        => CapabilityLearned?.Invoke(this, command);

    // ---- Compatibility surface ---------------------------------------------------

    /// <summary>Profile matched from the deck's Device Type response, when connected
    /// over Sony 9-pin and the code is recognized.</summary>
    public VtrDeviceProfile? DetectedProfile => (_current as Sony9PinController)?.DetectedProfile;

    /// <summary>Command support learned from the deck at runtime (null for the simulator).</summary>
    public IReadOnlyDictionary<TransportCommand, bool>? LearnedCapabilities
        => (_current as Sony9PinController)?.Capabilities.Snapshot();

    /// <summary>Raised when the connected deck reveals support (or lack of it) for a command.</summary>
    public event EventHandler<TransportCommand>? CapabilityLearned;

    // ---- IVtrController facade -------------------------------------------------

    public string DeviceDescription => _current.DeviceDescription;
    public bool IsConnected => _current.IsConnected;
    public VtrStatus CurrentStatus => _current.CurrentStatus;
    public TimeInformation CurrentTime => _current.CurrentTime;

    public event EventHandler<VtrStatus>? StatusChanged;
    public event EventHandler<TimeInformation>? TimeChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _current.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _current.DisconnectAsync(cancellationToken);

    public Task SendTransportCommandAsync(TransportCommand command, CancellationToken cancellationToken = default)
        => _current.SendTransportCommandAsync(command, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Detach(_current);
        if (!_currentIsSimulator)
        {
            await _current.DisposeAsync();
        }
        else
        {
            await _current.DisconnectAsync();
        }
    }
}