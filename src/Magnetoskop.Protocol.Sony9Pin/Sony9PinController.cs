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
    private VtrLinkHealth _linkHealth = new();
    private DateTimeOffset _connectedAt;
    private DateTimeOffset? _lastResponseAt;
    private string _deviceDescription;

    /// <summary>Command support learned from the deck's NAK responses at runtime.</summary>
    public DeviceCapabilities Capabilities { get; }

    /// <summary>Profile matched from the Device Type response, when recognized.</summary>
    public VtrDeviceProfile? DetectedProfile { get; private set; }

    /// <summary>Polling silence thresholds. Defaults are shared with time observations.</summary>
    public TimeSpan LinkStaleAfter { get; set; } = TimeInformation.DefaultStaleAfter;
    public TimeSpan LinkLostAfter { get; set; } = TimeInformation.DefaultLostAfter;

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

    public VtrLinkHealth LinkHealth => _linkHealth;

    public event EventHandler<VtrStatus>? StatusChanged;
    public event EventHandler<TimeInformation>? TimeChanged;
    public event EventHandler<VtrLinkHealth>? LinkHealthChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        await _transport.OpenAsync(_serialSettings, cancellationToken);
        _connectedAt = DateTimeOffset.UtcNow;
        _lastResponseAt = null;
        Publish(new VtrLinkHealth
        {
            State = VtrLinkState.Connecting,
            UpdatedAt = _connectedAt,
        });

        // Identify the deck. A NAK/timeout here is not fatal — some decks may not
        // implement Device Type Request — but a healthy response refines the description.
        try
        {
            var response = await _transceiver.ExchangeAsync(
                Sony9PinCommands.DeviceTypeRequest(), cancellationToken);
            RecordResponse(DateTimeOffset.UtcNow);
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
        var disconnectedAt = DateTimeOffset.UtcNow;
        Publish(_status with
        {
            IsConnected = false,
            Timestamp = disconnectedAt,
            Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        });
        Publish(new TimeInformation { Timestamp = disconnectedAt });
        Publish(new VtrLinkHealth
        {
            State = VtrLinkState.Disconnected,
            LastResponseAt = _lastResponseAt,
            UpdatedAt = disconnectedAt,
        });
        _logger.LogInformation("Disconnected from {Device}", _deviceDescription);
    }

    public async Task SendTransportCommandAsync(TransportCommand command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new VtrCommunicationException("Not connected to the recorder.");
        }

        if (command is TransportCommand.FrameStepForward or TransportCommand.FrameStepReverse)
        {
            await SendFrameStepAsync(command, cancellationToken);
            return;
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

    /// <summary>
    /// Native FRAME STEP (<c>20 14</c> / <c>20 24</c>) when the deck supports it;
    /// otherwise Cue Up TIMER-1 to current CTL ± 1 frame (Still).
    /// </summary>
    private async Task SendFrameStepAsync(TransportCommand command, CancellationToken cancellationToken)
    {
        var delta = command == TransportCommand.FrameStepForward ? 1 : -1;

        var tryNative = _profile.IsCommandSupported(command)
            && Capabilities.IsSupported(command) != false;

        if (tryNative)
        {
            try
            {
                var block = MapCommand(command);
                await ExchangeTransportAsync(command, block, cancellationToken);
                return;
            }
            catch (UnsupportedCommandException)
            {
                // NAK undefined — fall through to Cue Up ±1.
            }
        }

        await FrameStepViaCueUpAsync(delta, cancellationToken);
    }

    private async Task FrameStepViaCueUpAsync(int deltaFrames, CancellationToken cancellationToken)
    {
        if (_time.Ctl is not { } rawCtl)
        {
            throw new VtrCommunicationException(
                "CTL unavailable for frame step fallback (Cue Up ±1).");
        }

        var fps = _profile.FrameRate > 0 ? _profile.FrameRate : 25;
        var ctl = Timecode.InterpretAsSignedCtl(rawCtl, fps);
        var target = Timecode.FromFrameCount(ctl.ToFrameCount(fps) + deltaFrames, fps);

        _logger.LogInformation(
            "Frame Step unsupported; Cue Up CTL±1 → {Target} (delta {Delta}, from {Ctl})",
            target, deltaFrames, ctl);

        await CueUpAsync(target, CueUpTimerMode.Timer1, cancellationToken);
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

    public async Task CueUpAsync(
        Timecode timecode,
        CueUpTimerMode timerMode = CueUpTimerMode.TimeCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new VtrCommunicationException("Not connected to the recorder.");
        }

        const TransportCommand command = TransportCommand.CueUp;
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

        // Cue Up With Data uses the deck's timer mode (TC / Timer-1 / Timer-2).
        try
        {
            var timerResponse = await _transceiver.ExchangeAsync(
                Sony9PinCommands.TimerModeSelect(timerMode), cancellationToken);
            if (timerResponse is Sony9PinResponse.Ack)
            {
                _logger.LogInformation("Timer mode set to {Mode} before Cue Up", timerMode);
            }
            else
            {
                _logger.LogWarning(
                    "Timer Mode Select ({Mode}) before Cue Up returned {Response}; proceeding anyway",
                    timerMode, timerResponse);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timer Mode Select before Cue Up failed; proceeding with Cue Up");
        }

        var fps = _profile.FrameRate > 0 ? _profile.FrameRate : 25;
        var block = Sony9PinCommands.CueUpWithData(timecode, fps);
        _logger.LogInformation("Cue Up {Timecode} ({Mode}) → {Block}", timecode, timerMode, block);
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
        TransportCommand.FrameStepForward => Sony9PinCommands.FrameStepForward(),
        TransportCommand.FrameStepReverse => Sony9PinCommands.FrameStepReverse(),
        TransportCommand.Timer1Reset => Sony9PinCommands.Timer1Reset(),
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
        var startedAt = DateTimeOffset.UtcNow;
        var statusDue = startedAt;
        var timeDue = startedAt;
        var detailInterval = TimeSpan.FromMilliseconds(
            Math.Max(250, _profile.TimecodePollInterval.TotalMilliseconds * 3));
        var userBitsInterval = TimeSpan.FromMilliseconds(
            Math.Max(500, _profile.TimecodePollInterval.TotalMilliseconds * 5));
        var ltcDue = _profile.SupportsLtc ? startedAt + detailInterval / 4 : DateTimeOffset.MaxValue;
        var vitcDue = _profile.SupportsVitc ? startedAt + detailInterval * 3 / 4 : DateTimeOffset.MaxValue;
        var ltcUserBitsDue = _profile.SupportsLtc ? startedAt + userBitsInterval / 4 : DateTimeOffset.MaxValue;
        var vitcUserBitsDue = _profile.SupportsVitc ? startedAt + userBitsInterval * 3 / 4 : DateTimeOffset.MaxValue;

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
                if (now >= ltcDue)
                {
                    ltcDue = now + detailInterval;
                    await PollOptionalTimeAsync(TimeSenseRequest.LtcTime, "LTC", ct);
                }

                now = DateTimeOffset.UtcNow;
                if (now >= vitcDue)
                {
                    vitcDue = now + detailInterval;
                    await PollOptionalTimeAsync(TimeSenseRequest.VitcTime, "VITC", ct);
                }

                now = DateTimeOffset.UtcNow;
                if (now >= ltcUserBitsDue)
                {
                    ltcUserBitsDue = now + userBitsInterval;
                    await PollOptionalUserBitsAsync(TimeSenseRequest.LtcUserBits, "LTC user bits", ct);
                }

                now = DateTimeOffset.UtcNow;
                if (now >= vitcUserBitsDue)
                {
                    vitcUserBitsDue = now + userBitsInterval;
                    await PollOptionalUserBitsAsync(TimeSenseRequest.VitcUserBits, "VITC user bits", ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling cycle failed; will retry");
                UpdateLinkHealth(DateTimeOffset.UtcNow, ex.Message);
                // Back off briefly so a dead link does not spin.
                await Task.Delay(250, ct);
            }

            UpdateLinkHealth(DateTimeOffset.UtcNow);
            var nextDue = new[]
            {
                statusDue, timeDue, ltcDue, vitcDue, ltcUserBitsDue, vitcUserBitsDue,
            }.Min();
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
        var receivedAt = DateTimeOffset.UtcNow;
        var receivedTimestamp100ns = CaptureMonotonicClock.GetTimestamp100ns();
        RecordResponse(receivedAt);
        if (response is Sony9PinResponse.StatusData status)
        {
            Publish(StatusBitsParser.Parse(status.Bytes) with
            {
                Timestamp = receivedAt,
                Timestamp100ns = receivedTimestamp100ns,
            });
        }
    }

    private async Task PollTimeAsync(CancellationToken ct)
    {
        // Best available timecode (deck picks LTC/VITC/corrected LTC).
        var tcResponse = await _transceiver.ExchangeAsync(
            Sony9PinCommands.CurrentTimeSense(TimeSenseRequest.BestTimecode), ct);
        var tcReceipt = CaptureReceipt();
        RecordResponse(tcReceipt.ReceivedAt);
        if (tcResponse is Sony9PinResponse.TimeData tc)
        {
            Publish(ApplyTimeData(_time, tc, tcReceipt, makePrimary: true));
        }

        // CTL (Timer-1).
        var ctlResponse = await _transceiver.ExchangeAsync(
            Sony9PinCommands.CurrentTimeSense(TimeSenseRequest.Timer1), ct);
        var ctlReceipt = CaptureReceipt();
        RecordResponse(ctlReceipt.ReceivedAt);
        if (ctlResponse is Sony9PinResponse.TimeData ctl)
        {
            Publish(ApplyTimeData(_time, ctl, ctlReceipt, makePrimary: false));
        }
    }

    private async Task PollOptionalTimeAsync(
        TimeSenseRequest request,
        string label,
        CancellationToken ct)
    {
        try
        {
            var response = await _transceiver.ExchangeAsync(
                Sony9PinCommands.CurrentTimeSense(request), ct);
            var receipt = CaptureReceipt();
            RecordResponse(receipt.ReceivedAt);
            if (response is Sony9PinResponse.TimeData time)
            {
                Publish(ApplyTimeData(_time, time, receipt, makePrimary: false));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Optional registers are absent on some decks; they do not make the link unhealthy.
            _logger.LogDebug(ex, "Optional {Label} poll failed", label);
        }
    }

    private async Task PollOptionalUserBitsAsync(
        TimeSenseRequest request,
        string label,
        CancellationToken ct)
    {
        try
        {
            var response = await _transceiver.ExchangeAsync(
                Sony9PinCommands.CurrentTimeSense(request), ct);
            var receipt = CaptureReceipt();
            RecordResponse(receipt.ReceivedAt);
            if (response is Sony9PinResponse.UserBitsData userBits)
            {
                Publish(ApplyUserBitsData(_time, userBits, receipt));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Optional {Label} poll failed", label);
        }
    }

    private static TimeInformation ApplyTimeData(
        TimeInformation current,
        Sony9PinResponse.TimeData response,
        ObservationReceipt receipt,
        bool makePrimary)
        => response.Kind switch
        {
            TimeDataKind.Timer1 => current with
            {
                Ctl = response.Timecode,
                CtlSource = TimecodeSource.Ctl,
                CtlReceivedAt = receipt.ReceivedAt,
                CtlReceivedTimestamp100ns = receipt.Timestamp100ns,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.Timer2 => current with
            {
                Ctl = response.Timecode,
                CtlSource = TimecodeSource.Ctl2,
                CtlReceivedAt = receipt.ReceivedAt,
                CtlReceivedTimestamp100ns = receipt.Timestamp100ns,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.LtcTime => current with
            {
                Ltc = response.Timecode,
                LtcSource = TimecodeSource.Ltc,
                LtcReceivedAt = receipt.ReceivedAt,
                LtcReceivedTimestamp100ns = receipt.Timestamp100ns,
                PrimarySource = makePrimary ? TimecodeSource.Ltc : current.PrimarySource,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.CorrectedLtcTime => current with
            {
                Ltc = response.Timecode,
                LtcSource = TimecodeSource.CorrectedLtc,
                LtcReceivedAt = receipt.ReceivedAt,
                LtcReceivedTimestamp100ns = receipt.Timestamp100ns,
                PrimarySource = makePrimary ? TimecodeSource.CorrectedLtc : current.PrimarySource,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.VitcTime => current with
            {
                Vitc = response.Timecode,
                VitcSource = TimecodeSource.Vitc,
                VitcReceivedAt = receipt.ReceivedAt,
                VitcReceivedTimestamp100ns = receipt.Timestamp100ns,
                PrimarySource = makePrimary ? TimecodeSource.Vitc : current.PrimarySource,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.HoldVitcTime => current with
            {
                Vitc = response.Timecode,
                VitcSource = TimecodeSource.HoldVitc,
                VitcReceivedAt = receipt.ReceivedAt,
                VitcReceivedTimestamp100ns = receipt.Timestamp100ns,
                PrimarySource = makePrimary ? TimecodeSource.HoldVitc : current.PrimarySource,
                Timestamp = receipt.ReceivedAt,
            },
            _ => current,
        };

    private static TimeInformation ApplyUserBitsData(
        TimeInformation current,
        Sony9PinResponse.UserBitsData response,
        ObservationReceipt receipt)
        => response.Kind switch
        {
            TimeDataKind.LtcUserBits => current with
            {
                LtcUserBits = response.UserBits,
                LtcUserBitsSource = TimecodeSource.Ltc,
                LtcUserBitsReceivedAt = receipt.ReceivedAt,
                LtcUserBitsReceivedTimestamp100ns = receipt.Timestamp100ns,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.HoldLtcUserBits => current with
            {
                LtcUserBits = response.UserBits,
                LtcUserBitsSource = TimecodeSource.HoldLtc,
                LtcUserBitsReceivedAt = receipt.ReceivedAt,
                LtcUserBitsReceivedTimestamp100ns = receipt.Timestamp100ns,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.VitcUserBits => current with
            {
                VitcUserBits = response.UserBits,
                VitcUserBitsSource = TimecodeSource.Vitc,
                VitcUserBitsReceivedAt = receipt.ReceivedAt,
                VitcUserBitsReceivedTimestamp100ns = receipt.Timestamp100ns,
                Timestamp = receipt.ReceivedAt,
            },
            TimeDataKind.HoldVitcUserBits => current with
            {
                VitcUserBits = response.UserBits,
                VitcUserBitsSource = TimecodeSource.HoldVitc,
                VitcUserBitsReceivedAt = receipt.ReceivedAt,
                VitcUserBitsReceivedTimestamp100ns = receipt.Timestamp100ns,
                Timestamp = receipt.ReceivedAt,
            },
            _ => current,
        };

    private static ObservationReceipt CaptureReceipt()
        => new(DateTimeOffset.UtcNow, CaptureMonotonicClock.GetTimestamp100ns());

    private readonly record struct ObservationReceipt(
        DateTimeOffset ReceivedAt,
        long Timestamp100ns);

    private void RecordResponse(DateTimeOffset receivedAt)
    {
        _lastResponseAt = receivedAt;
        if (_linkHealth.State == VtrLinkState.Online)
        {
            _linkHealth = _linkHealth with
            {
                LastResponseAt = receivedAt,
                UpdatedAt = receivedAt,
                Error = null,
            };
        }
        else
        {
            Publish(new VtrLinkHealth
            {
                State = VtrLinkState.Online,
                LastResponseAt = receivedAt,
                UpdatedAt = receivedAt,
            });
        }
    }

    private void UpdateLinkHealth(DateTimeOffset now, string? error = null)
    {
        if (!IsConnected) return;

        var reference = _lastResponseAt ?? _connectedAt;
        var age = now - reference;
        var state = age > LinkLostAfter
            ? VtrLinkState.Lost
            : age > LinkStaleAfter
                ? VtrLinkState.Stale
                : _lastResponseAt is null
                    ? VtrLinkState.Connecting
                    : VtrLinkState.Online;

        if (state == _linkHealth.State && error == _linkHealth.Error) return;

        Publish(new VtrLinkHealth
        {
            State = state,
            LastResponseAt = _lastResponseAt,
            UpdatedAt = now,
            Error = state is VtrLinkState.Stale or VtrLinkState.Lost ? error : null,
        });

        if (state == VtrLinkState.Lost && _status.IsConnected)
        {
            Publish(_status with
            {
                IsConnected = false,
                Timestamp = now,
                Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
            });
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

    private void Publish(VtrLinkHealth health)
    {
        _linkHealth = health;
        LinkHealthChanged?.Invoke(this, health);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _transport.DisposeAsync();
    }
}
