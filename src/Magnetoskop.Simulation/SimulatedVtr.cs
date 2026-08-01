using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Simulation;

/// <summary>
/// A simulated Sony-style videotape recorder: full transport state machine with
/// synthesized CTL/LTC/VITC counters. Used to develop and test the application
/// without hardware.
/// </summary>
public sealed class SimulatedVtr : IVtrController
{
    private const int FrameRate = 25; // PAL
    private const double FastWindSpeed = 32.0; // x play speed

    private readonly ILogger<SimulatedVtr> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private TransportState _transport = TransportState.Stopped;
    private double _tapePositionFrames = 0; // absolute tape position in frames
    private bool _tapeOut;
    private VtrStatus _status = new();
    private TimeInformation _time = new();

    // The simulated tape starts with LTC/VITC at 01:00:00:00 at tape position 0.
    private const long TimecodeOffsetFrames = 1L * 60 * 60 * FrameRate;
    private const long TapeLengthFrames = 95L * 60 * FrameRate; // 95-minute tape

    public SimulatedVtr(ILogger<SimulatedVtr> logger)
    {
        _logger = logger;
    }

    public string DeviceDescription => "Simulated VTR (PAL, 25 fps)";

    public bool IsConnected { get; private set; }

    public VtrStatus CurrentStatus => _status;

    public TimeInformation CurrentTime => _time;

    public event EventHandler<VtrStatus>? StatusChanged;
    public event EventHandler<TimeInformation>? TimeChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return Task.CompletedTask;

        IsConnected = true;
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => RunAsync(_pollCts.Token), CancellationToken.None);
        _logger.LogInformation("Simulated VTR connected");
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;

        IsConnected = false;
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync();
        }
        if (_pollTask is not null)
        {
            try { await _pollTask; } catch (OperationCanceledException) { }
        }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
        _logger.LogInformation("Simulated VTR disconnected");
    }

    public async Task SendTransportCommandAsync(TransportCommand command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new VtrCommunicationException("Not connected to the simulated VTR.");
        }

        // Simulate serial round-trip latency (spec: response within 9 ms).
        await Task.Delay(8, cancellationToken);

        lock (_gate)
        {
            switch (command)
            {
                case TransportCommand.Play:
                    RequireTape();
                    _transport = TransportState.Playing;
                    break;
                case TransportCommand.Stop:
                    if (_transport == TransportState.Ejecting)
                        break;
                    _transport = _tapeOut ? TransportState.Unknown : TransportState.Stopped;
                    break;
                case TransportCommand.FastForward:
                    RequireTape();
                    _transport = TransportState.FastForwarding;
                    break;
                case TransportCommand.Rewind:
                    RequireTape();
                    _transport = TransportState.Rewinding;
                    break;
                case TransportCommand.Eject:
                    RequireTape();
                    _transport = TransportState.Ejecting;
                    break;
                default:
                    throw new UnsupportedCommandException(
                        $"The simulated VTR does not implement '{command}' yet.");
            }
        }

        _logger.LogInformation("Simulated VTR transport command: {Command} -> {State}", command, _transport);
    }

    private void RequireTape()
    {
        if (_tapeOut)
        {
            throw new VtrCommunicationException("No tape loaded in the simulated VTR.");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        const int tickMs = 40; // 25 Hz, one PAL frame per tick at play speed
        var lastTick = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(tickMs, ct);
            var now = Environment.TickCount64;
            var elapsedSeconds = (now - lastTick) / 1000.0;
            lastTick = now;

            lock (_gate)
            {
                Advance(elapsedSeconds);
            }

            PublishSnapshots();
        }
    }

    private void Advance(double elapsedSeconds)
    {
        var deltaFrames = elapsedSeconds * FrameRate;
        switch (_transport)
        {
            case TransportState.Playing:
            case TransportState.Recording:
                _tapePositionFrames += deltaFrames;
                break;
            case TransportState.FastForwarding:
                _tapePositionFrames += deltaFrames * FastWindSpeed;
                break;
            case TransportState.Rewinding:
                _tapePositionFrames -= deltaFrames * FastWindSpeed;
                break;
            case TransportState.Ejecting:
                _tapeOut = true;
                _transport = TransportState.Unknown;
                break;
        }

        if (_tapePositionFrames < 0)
        {
            _tapePositionFrames = 0;
            if (_transport == TransportState.Rewinding) _transport = TransportState.Stopped;
        }

        if (_tapePositionFrames > TapeLengthFrames)
        {
            _tapePositionFrames = TapeLengthFrames;
            if (_transport is TransportState.Playing or TransportState.FastForwarding or TransportState.Recording)
            {
                _transport = TransportState.Stopped;
            }
        }
    }

    private void PublishSnapshots()
    {
        TransportState transport;
        double pos;
        bool tapeOut;
        lock (_gate)
        {
            transport = _transport;
            pos = _tapePositionFrames;
            tapeOut = _tapeOut;
        }

        var positionFrames = (long)pos;
        var ctl = Timecode.FromFrameCount(positionFrames, FrameRate);
        var tc = Timecode.FromFrameCount(positionFrames + TimecodeOffsetFrames, FrameRate);

        // LTC is unreadable in fast wind (like a real deck at high speed); VITC only near play speed.
        var isPlaySpeed = transport is TransportState.Playing or TransportState.Recording;
        var isFastWind = transport is TransportState.FastForwarding or TransportState.Rewinding;

        var status = new VtrStatus
        {
            IsConnected = IsConnected,
            Transport = transport,
            TapeOut = tapeOut,
            ServoLock = isPlaySpeed,
            NearEndOfTape = pos > TapeLengthFrames - 5L * 60 * FrameRate,
            EndOfTape = pos >= TapeLengthFrames,
            TapeReverse = transport == TransportState.Rewinding,
        };

        var time = new TimeInformation
        {
            Ctl = ctl,
            Ltc = tapeOut ? null : (isFastWind ? null : tc),
            Vitc = tapeOut ? null : (isPlaySpeed ? tc : null),
            LtcUserBits = tapeOut ? null : new UserBits(0x20, 0x26, 0x01, 0x01),
            VitcUserBits = tapeOut ? null : new UserBits(0x20, 0x26, 0x01, 0x01),
            PrimarySource = tapeOut
                ? TimecodeSource.Ctl
                : isPlaySpeed ? TimecodeSource.Ltc
                : isFastWind ? TimecodeSource.CorrectedLtc
                : TimecodeSource.Ctl,
        };

        _status = status;
        _time = time;
        StatusChanged?.Invoke(this, status);
        TimeChanged?.Invoke(this, time);
    }

    /// <summary>Re-inserts the simulated tape after an eject (test helper / UI action).</summary>
    public void InsertTape()
    {
        lock (_gate)
        {
            _tapeOut = false;
            _tapePositionFrames = 0;
            _transport = TransportState.Stopped;
        }
        _logger.LogInformation("Simulated tape inserted");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}