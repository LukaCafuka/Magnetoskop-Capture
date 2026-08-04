using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// High-level Sony 9-pin recorder controller:
/// - opens the serial transport with the profile's settings,
/// - identifies the device (Device Type Request),
/// - runs a background polling loop (status + timecode + user bits),
/// - executes transport commands with priority over polling,
/// - maps NAK "undefined command" to <see cref="UnsupportedCommandException"/>.
/// The single-flight guarantee lives in <see cref="Sony9PinTransceiver"/>.
/// </summary>
public sealed class Sony9PinController : IVtrController
{
    private readonly ISerialTransport _transport;
    private readonly Sony9PinTransceiver _transceiver;
    private readonly VtrDeviceProfile _profile;
    private readonly SerialSettings _serialSettings;
    private readonly ILogger<Sony9PinController> _logger;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private VtrStatus _status = new();
    private TimeInformation _time = new();
    private string _deviceDescription;

    /// <summary>Command support learned from the deck's NAK responses at runtime.</summary>
    public DeviceCapabilities Capabilities { get; }

    /// <summary>Profile matched from the Device Type response, when recognized.</summary>
    public VtrDeviceProfile? DetectedProfile { get; private set; }

    public Sony9PinController(
        ISerialTransport transport,
        VtrDeviceProfile profile,
        SerialSettings serialSettings,
        ILogger<Sony9PinController> logger)
    {
        _transport = transport;
        _profile = profile;
        _serialSettings = serialSettings;
        _logger = logger;
        _transceiver = new Sony9PinTransceiver(transport, logger)
        {
            ResponseTimeout = profile.ResponseTimeout,
            MaxRetries = profile.MaxRetries,
        };
        _deviceDescription = profile.DisplayName;
        Capabilities = new DeviceCapabilities(logger);
    }

    public string DeviceDescription => _deviceDescription;

    public bool IsConnected { get; private set; }

    public VtrStatus CurrentStatus => _status;

    public TimeInformation CurrentTime => _time;

    public event EventHandler<VtrStatus>? StatusChanged;
    public event EventHandler<TimeInformation>? TimeChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        await _transport.OpenAsync(_serialSettings, cancellationToken);

        // Identify the deck. A NAK/timeout here is not fatal — some decks may not
        // implement Device Type Request — but a healthy response refines the description.
        try
        {
            var response = await _transceiver.ExchangeAsync(
                Sony9PinCommands.DeviceTypeRequest(), cancellationToken);
            if (response is Sony9PinResponse.DeviceType dt)
            {
                DetectedProfile = KnownDeviceProfiles.FromDeviceTypeCode(dt.Byte1, dt.Byte2);
                var detectedName = DetectedProfile.Id != VtrDeviceProfile.Generic.Id
                    ? DetectedProfile.DisplayName
                    : _profile.DisplayName;
                _deviceDescription = $"{detectedName} (device type {dt.Code})";
                _logger.LogInformation("Device identified: type {Code} → profile {Profile}",
                    dt.Code, DetectedProfile.Id);
            }
            else
            {
                _logger.LogWarning("Device Type Request answered with {Response}", response.GetType().Name);
            }
        }
        catch (VtrCommunicationException ex)
        {
            _logger.LogWarning(ex, "Device Type Request failed; continuing with profile defaults");
        }

        IsConnected = true;
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
        _logger.LogInformation("Connected to {Device} on {Port}", _deviceDescription, _serialSettings.PortName);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;

        IsConnected = false;
        Capabilities.Reset();
        if (_pollCts is not null) await _pollCts.CancelAsync();
        if (_pollTask is not null)
        {
            try { await _pollTask; } catch (OperationCanceledException) { }
        }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;

        await _transport.CloseAsync(cancellationToken);
        Publish(_status with { IsConnected = false });
        _logger.LogInformation("Disconnected from {Device}", _deviceDescription);
    }

    public async Task SendTransportCommandAsync(TransportCommand command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new VtrCommunicationException("Not connected to the recorder.");
        }

        if (!_profile.IsCommandSupported(command))
        {
            throw new UnsupportedCommandException(
                $"Profile '{_profile.DisplayName}' marks '{command}' as unsupported.");
        }

        if (Capabilities.IsSupported(command) == false)
        {
            throw new UnsupportedCommandException(
                $"The recorder previously reported '{command}' as an undefined command.");
        }

        var block = MapCommand(command);
        await ExchangeTransportAsync(command, block, cancellationToken);
    }

    public async Task SendVariableSpeedAsync(
        VariableSpeedMode mode,
        bool forward,
        byte speed,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new VtrCommunicationException("Not connected to the recorder.");
        }

        var command = VariableSpeedEncoding.ToTransportCommand(mode, forward);
        if (!_profile.IsCommandSupported(command))
        {
            throw new UnsupportedCommandException(
                $"Profile '{_profile.DisplayName}' marks '{command}' as unsupported.");
        }

        if (Capabilities.IsSupported(command) == false)
        {
            throw new UnsupportedCommandException(
                $"The recorder previously reported '{command}' as an undefined command.");
        }

        var block = MapVariableSpeed(mode, forward, speed);
        await ExchangeTransportAsync(command, block, cancellationToken);
    }

    private async Task ExchangeTransportAsync(
        TransportCommand command, CommandBlock block, CancellationToken cancellationToken)
    {
        var response = await _transceiver.ExchangeAsync(block, cancellationToken);

        switch (response)
        {
            case Sony9PinResponse.Ack:
                Capabilities.RecordSupported(command);
                _logger.LogInformation("Command {Command} acknowledged", command);
                return;
            case Sony9PinResponse.Nak { IsUndefinedCommand: true }:
                Capabilities.RecordUnsupported(command);
                throw new UnsupportedCommandException(
                    $"The recorder reports '{command}' ({block}) as an undefined command.");
            case Sony9PinResponse.Nak nak:
                throw new VtrCommunicationException($"Command {command} rejected: {nak.Error}.");
            default:
                throw new VtrCommunicationException(
                    $"Unexpected response {response.Raw} to command {command}.");
        }
    }

    private static CommandBlock MapCommand(TransportCommand command) => command switch
    {
        TransportCommand.Play => Sony9PinCommands.Play(),
        TransportCommand.Stop => Sony9PinCommands.Stop(),
        TransportCommand.FastForward => Sony9PinCommands.FastForward(),
        TransportCommand.Rewind => Sony9PinCommands.Rewind(),
        TransportCommand.Eject => Sony9PinCommands.Eject(),
        TransportCommand.Pause => Sony9PinCommands.Pause(),
        TransportCommand.Record => Sony9PinCommands.Record(),
        TransportCommand.StandbyOn => Sony9PinCommands.StandbyOn(),
        TransportCommand.StandbyOff => Sony9PinCommands.StandbyOff(),
        TransportCommand.Preroll => Sony9PinCommands.Preroll(),
        _ => throw new UnsupportedCommandException(
            $"Transport command '{command}' has no Sony 9-pin mapping yet."),
    };

    private static CommandBlock MapVariableSpeed(VariableSpeedMode mode, bool forward, byte speed)
        => (mode, forward) switch
        {
            (VariableSpeedMode.Jog, true) => Sony9PinCommands.JogForward(speed),
            (VariableSpeedMode.Jog, false) => Sony9PinCommands.JogReverse(speed),
            (VariableSpeedMode.Shuttle, true) => Sony9PinCommands.ShuttleForward(speed),
            (VariableSpeedMode.Shuttle, false) => Sony9PinCommands.ShuttleReverse(speed),
            _ => throw new UnsupportedCommandException($"Unsupported variable-speed mode '{mode}'."),
        };

    // ---- Polling -----------------------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var statusDue = DateTimeOffset.MinValue;
        var timeDue = DateTimeOffset.MinValue;
        var userBitsDue = DateTimeOffset.MinValue;
        var userBitsInterval = TimeSpan.FromMilliseconds(
            Math.Max(500, _profile.TimecodePollInterval.TotalMilliseconds * 5));

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (now >= statusDue)
                {
                    statusDue = now + _profile.StatusPollInterval;
                    await PollStatusAsync(ct);
                }

                now = DateTimeOffset.UtcNow;
                if (now >= timeDue)
                {
                    timeDue = now + _profile.TimecodePollInterval;
                    await PollTimeAsync(ct);
                }

                now = DateTimeOffset.UtcNow;
                if (now >= userBitsDue)
                {
                    userBitsDue = now + userBitsInterval;
                    await PollUserBitsAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling cycle failed; will retry");
                // Back off briefly so a dead link does not spin.
                await Task.Delay(250, ct);
            }

            var nextDue = new[] { statusDue, timeDue, userBitsDue }.Min();
            var delay = nextDue - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task PollStatusAsync(CancellationToken ct)
    {
        var response = await _transceiver.ExchangeAsync(Sony9PinCommands.StatusSense(), ct);
        if (response is Sony9PinResponse.StatusData status)
        {
            Publish(StatusBitsParser.Parse(status.Bytes) with { Timestamp = DateTimeOffset.UtcNow });
        }
    }

    private async Task PollTimeAsync(CancellationToken ct)
    {
        // Best available timecode (deck picks LTC/VITC/corrected LTC).
        var tcResponse = await _transceiver.ExchangeAsync(
            Sony9PinCommands.CurrentTimeSense(TimeSenseRequest.BestTimecode), ct);

        // CTL (Timer-1).
        var ctlResponse = await _transceiver.ExchangeAsync(
            Sony9PinCommands.CurrentTimeSense(TimeSenseRequest.Timer1), ct);

        var time = _time;

        if (tcResponse is Sony9PinResponse.TimeData tc)
        {
            time = tc.Kind switch
            {
                TimeDataKind.LtcTime => time with { Ltc = tc.Timecode, PrimarySource = TimecodeSource.Ltc },
                TimeDataKind.VitcTime or TimeDataKind.HoldVitcTime
                    => time with { Vitc = tc.Timecode, PrimarySource = TimecodeSource.Vitc },
                TimeDataKind.CorrectedLtcTime
                    => time with { Ltc = tc.Timecode, PrimarySource = TimecodeSource.CorrectedLtc },
                _ => time,
            };
        }

        if (ctlResponse is Sony9PinResponse.TimeData { Kind: TimeDataKind.Timer1 } ctl)
        {
            time = time with { Ctl = ctl.Timecode };
        }

        Publish(time with { Timestamp = DateTimeOffset.UtcNow });
    }

    private async Task PollUserBitsAsync(CancellationToken ct)
    {
        if (!_profile.SupportsLtc) return;

        var response = await _transceiver.ExchangeAsync(
            Sony9PinCommands.CurrentTimeSense(TimeSenseRequest.LtcUserBits), ct);
        if (response is Sony9PinResponse.UserBitsData ub)
        {
            var time = ub.Kind switch
            {
                TimeDataKind.LtcUserBits or TimeDataKind.HoldLtcUserBits
                    => _time with { LtcUserBits = ub.UserBits },
                TimeDataKind.VitcUserBits or TimeDataKind.HoldVitcUserBits
                    => _time with { VitcUserBits = ub.UserBits },
                _ => _time,
            };
            Publish(time with { Timestamp = DateTimeOffset.UtcNow });
        }
    }

    private void Publish(VtrStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void Publish(TimeInformation time)
    {
        _time = time;
        TimeChanged?.Invoke(this, time);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _transport.DisposeAsync();
    }
}