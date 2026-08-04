using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;

namespace Magnetoskop.Simulation;

/// <summary>
/// A wire-level simulated Sony 9-pin deck behind <see cref="ISerialTransport"/>:
/// it receives raw command blocks and answers with raw response bytes (ACK, NAK,
/// device type, status data, BCD timecode), driven by a <see cref="DeckPersonality"/>.
///
/// The framing/checksum logic here is deliberately implemented independently of
/// Magnetoskop.Protocol.Sony9Pin so compatibility tests exercise the real protocol
/// stack against a foreign implementation rather than against itself.
/// </summary>
public sealed class SimulatedDeckTransport : ISerialTransport
{
    private const int FrameRate = 25; // PAL
    private const double FastWindSpeed = 32.0;
    private const long TimecodeOffsetFrames = 1L * 60 * 60 * FrameRate; // LTC starts at 01:00:00:00
    private const long TapeLengthFrames = 95L * 60 * FrameRate;
    /// <summary>Starting position 10 minutes into the tape so short rewinds do not
    /// immediately hit the beginning-of-tape stop.</summary>
    private const long InitialPositionFrames = 10L * 60 * FrameRate;

    private readonly DeckPersonality _personality;
    private readonly Channel<byte> _output = Channel.CreateUnbounded<byte>();
    private readonly List<byte> _rxBuffer = new();
    private readonly object _gate = new();

    // Deck state.
    private TransportState _transport = TransportState.Stopped;
    private double _tapePositionFrames = InitialPositionFrames;
    private double _variablePlayRate;
    private bool _tapeOut;
    private long _lastAdvanceTicks = Environment.TickCount64;
    private int _commandsSeen;
    private CueUpTimerMode _timerMode = CueUpTimerMode.TimeCode;

    public SimulatedDeckTransport(DeckPersonality personality)
    {
        _personality = personality;
    }

    public DeckPersonality Personality => _personality;

    /// <summary>Current simulated transport state (test observability).</summary>
    public TransportState TransportState
    {
        get { lock (_gate) { AdvanceTape(); return _transport; } }
    }

    public bool IsOpen { get; private set; }

    /// <summary>Serial settings the master opened the port with (test observability).</summary>
    public SerialSettings? OpenedSettings { get; private set; }

    public Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        OpenedSettings = settings;
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        IsOpen = false;
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new VtrCommunicationException("Port is not open.");

        List<byte[]> responses = new();
        lock (_gate)
        {
            _rxBuffer.AddRange(buffer.ToArray());
            while (TryTakeCommand(out var cmd1, out var cmd2, out var data, out var checksumOk))
            {
                var response = HandleCommand(cmd1, cmd2, data, checksumOk);
                if (response is not null) responses.Add(response);
            }
        }

        foreach (var response in responses)
        {
            if (_personality.ResponseLatency > TimeSpan.Zero)
            {
                await Task.Delay(_personality.ResponseLatency, cancellationToken);
            }
            foreach (var b in response)
            {
                _output.Writer.TryWrite(b);
            }
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;

        // Block for the first byte, then drain whatever else is immediately available.
        if (!await _output.Reader.WaitToReadAsync(cancellationToken))
        {
            return 0;
        }

        var count = 0;
        while (count < buffer.Length && _output.Reader.TryRead(out var b))
        {
            buffer.Span[count++] = b;
        }
        return count;
    }

    public void DiscardInput()
    {
        while (_output.Reader.TryRead(out _)) { }
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the simulated cassette (drives the tape-out status bit).</summary>
    public void EjectTape()
    {
        lock (_gate)
        {
            _tapeOut = true;
            _transport = TransportState.Stopped;
        }
    }

    public void InsertTape()
    {
        lock (_gate)
        {
            _tapeOut = false;
            _tapePositionFrames = InitialPositionFrames;
            _transport = TransportState.Stopped;
        }
    }

    // ---- Deck-side framing (independent of the protocol project) --------------

    private bool TryTakeCommand(out byte cmd1, out byte cmd2, out byte[] data, out bool checksumOk)
    {
        cmd1 = 0; cmd2 = 0; data = Array.Empty<byte>(); checksumOk = false;
        if (_rxBuffer.Count < 3) return false;

        var dataCount = _rxBuffer[0] & 0x0F;
        var blockLength = 2 + dataCount + 1;
        if (_rxBuffer.Count < blockLength) return false;

        cmd1 = _rxBuffer[0];
        cmd2 = _rxBuffer[1];
        data = _rxBuffer.GetRange(2, dataCount).ToArray();

        var sum = 0;
        for (var i = 0; i < blockLength - 1; i++) sum += _rxBuffer[i];
        checksumOk = (byte)sum == _rxBuffer[blockLength - 1];

        _rxBuffer.RemoveRange(0, blockLength);
        return true;
    }

    private static byte[] Frame(byte cmd1, byte cmd2, params byte[] data)
    {
        var block = new byte[2 + data.Length + 1];
        block[0] = (byte)(cmd1 & 0xF0 | data.Length);
        block[1] = cmd2;
        data.CopyTo(block, 2);
        var sum = 0;
        for (var i = 0; i < block.Length - 1; i++) sum += block[i];
        block[^1] = (byte)sum;
        return block;
    }

    private static byte[] Ack() => Frame(0x10, 0x01);
    private static byte[] Nak(byte errorBits) => Frame(0x10, 0x12, errorBits);

    // ---- Command handling ------------------------------------------------------

    private byte[]? HandleCommand(byte cmd1, byte cmd2, byte[] data, bool checksumOk)
    {
        _commandsSeen++;
        if (_commandsSeen <= _personality.DropFirstNCommands)
        {
            return null; // exercise the master's timeout/retry path
        }

        if (!checksumOk)
        {
            return Nak(0x04); // checksum error
        }

        AdvanceTape();

        return (cmd1 & 0xF0, cmd2) switch
        {
            (0x00, 0x11) => HandleDeviceTypeRequest(),
            (0x00, 0x0C) or (0x00, 0x1D) => Ack(), // local disable / enable
            // Cue Up With Data is 24 31 (group 2, 4 data bytes) — before variable-speed catch-all.
            (0x20, 0x31) when data.Length >= 4 => HandleCueUp(data),
            (0x20, _) when data.Length > 0 => HandleVariableSpeed(cmd2, data[0]),
            (0x20, _) => HandleTransport(cmd2),
            (0x40, 0x36) when data.Length >= 1 => HandleTimerModeSelect(data[0]),
            (0x60, 0x20) => HandleStatusSense(data),
            (0x60, 0x0C) => HandleTimeSense(data),
            _ => Nak(0x01), // undefined command
        };
    }

    private byte[] HandleTimerModeSelect(byte mode)
    {
        _timerMode = mode switch
        {
            0x01 => CueUpTimerMode.Timer1,
            _ => CueUpTimerMode.TimeCode,
        };
        return Ack();
    }

    private byte[] HandleCueUp(ReadOnlySpan<byte> data)
    {
        if (!_personality.AcceptsTransportCommand(TransportCommand.CueUp))
        {
            return Nak(0x01);
        }

        if (!_tapeOut)
        {
            var tc = DecodeBcdTimecode(data);
            long pos;
            if (_timerMode == CueUpTimerMode.Timer1)
            {
                pos = tc.ToFrameCount(FrameRate);
            }
            else
            {
                pos = tc.ToFrameCount(FrameRate) - TimecodeOffsetFrames;
            }

            _tapePositionFrames = Math.Clamp(pos, 0, TapeLengthFrames);
            _transport = TransportState.Still;
            _variablePlayRate = 0;
        }

        return Ack();
    }

    private static Timecode DecodeBcdTimecode(ReadOnlySpan<byte> data)
    {
        static int FromBcd(byte b) => (b >> 4 & 0x0F) * 10 + (b & 0x0F);
        var negative = (data[3] & 0x40) != 0 || (data[3] & 0x80) != 0;
        return new Timecode(
            Hours: FromBcd((byte)(data[3] & 0x3F)),
            Minutes: FromBcd(data[2]),
            Seconds: FromBcd(data[1]),
            Frames: FromBcd((byte)(data[0] & 0x3F)),
            DropFrame: (data[0] & 0x40) != 0,
            ColorFrame: (data[0] & 0x80) != 0,
            IsNegative: negative);
    }

    private byte[] HandleDeviceTypeRequest()
        => _personality.RespondsToDeviceTypeRequest
            ? Frame(0x12, 0x11, _personality.DeviceTypeByte1, _personality.DeviceTypeByte2)
            : Nak(0x01);

    private byte[] HandleTransport(byte cmd2)
    {
        var command = cmd2 switch
        {
            0x00 => TransportCommand.Stop,
            0x01 => TransportCommand.Play,
            0x02 => TransportCommand.Record,
            0x04 => TransportCommand.StandbyOff,
            0x05 => TransportCommand.StandbyOn,
            0x0F => TransportCommand.Eject,
            0x10 => TransportCommand.FastForward,
            0x20 => TransportCommand.Rewind,
            0x30 => TransportCommand.Preroll,
            _ => (TransportCommand?)null,
        };

        if (command is null || !_personality.AcceptsTransportCommand(command.Value))
        {
            return Nak(0x01); // undefined command on this deck
        }

        // A real deck acknowledges the command; without a cassette the transport
        // simply stays put and the status bits show CASSETTE OUT.
        if (!_tapeOut)
        {
            _transport = command switch
            {
                TransportCommand.Play => TransportState.Playing,
                TransportCommand.Record => TransportState.Recording,
                TransportCommand.Stop => TransportState.Stopped,
                TransportCommand.FastForward => TransportState.FastForwarding,
                TransportCommand.Rewind => TransportState.Rewinding,
                TransportCommand.Eject => TransportState.Stopped,
                _ => _transport,
            };
            _variablePlayRate = 0;
            if (command == TransportCommand.Eject)
            {
                _tapeOut = true;
            }
        }
        return Ack();
    }

    private byte[] HandleVariableSpeed(byte cmd2, byte speed)
    {
        var (command, forward) = cmd2 switch
        {
            0x11 => (TransportCommand.JogForward, true),
            0x21 => (TransportCommand.JogReverse, false),
            0x13 => (TransportCommand.ShuttleForward, true),
            0x23 => (TransportCommand.ShuttleReverse, false),
            _ => ((TransportCommand?)null, true),
        };

        if (command is null || !_personality.AcceptsTransportCommand(command.Value))
        {
            return Nak(0x01);
        }

        if (!_tapeOut)
        {
            var rate = VariableSpeedEncoding.ToPlayRate(speed);
            if (rate <= 0)
            {
                _transport = TransportState.Still;
                _variablePlayRate = 0;
            }
            else
            {
                _transport = command is TransportCommand.JogForward or TransportCommand.JogReverse
                    ? TransportState.Jog
                    : TransportState.Shuttle;
                _variablePlayRate = forward ? rate : -rate;
            }
        }

        return Ack();
    }

    private byte[] HandleStatusSense(byte[] data)
    {
        var start = data.Length > 0 ? data[0] >> 4 : 0;
        var requested = data.Length > 0 ? data[0] & 0x0F : 10;
        var available = Math.Max(0, _personality.StatusByteCount - start);
        var count = Math.Min(requested, available);

        var status = BuildStatusBytes();
        var slice = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            slice[i] = index < status.Length ? status[index] : (byte)0;
        }
        return Frame(0x70, 0x20, slice);
    }

    private byte[] BuildStatusBytes()
    {
        var isPlaySpeed = _transport is TransportState.Playing or TransportState.Recording;
        var isNearEot = _tapePositionFrames > TapeLengthFrames - 5L * 60 * FrameRate;
        var isEot = _tapePositionFrames >= TapeLengthFrames;

        byte d0 = 0;
        if (_tapeOut) d0 |= 0x20;

        byte d1 = _transport switch
        {
            TransportState.Playing => 0x01,
            TransportState.Recording => 0x03, // record + play bits (record has priority)
            TransportState.FastForwarding => 0x04,
            TransportState.Rewinding => 0x08,
            TransportState.Stopped => 0x20,
            TransportState.Still => 0x00,
            _ => 0x00,
        };

        byte d2 = 0;
        if (isPlaySpeed) d2 |= 0x80; // servo lock
        if (_transport == TransportState.Shuttle) d2 |= 0x20;
        if (_transport == TransportState.Jog) d2 |= 0x10;
        if (_transport == TransportState.Still) d2 |= 0x02;
        if (_transport == TransportState.Rewinding || _variablePlayRate < 0) d2 |= 0x04;

        byte d8 = 0;
        if (isEot) d8 |= 0x10;
        else if (isNearEot) d8 |= 0x20;

        return new byte[] { d0, d1, d2, 0, 0, 0, 0, 0, d8, 0 };
    }

    private byte[] HandleTimeSense(byte[] data)
    {
        if (data.Length < 1) return Nak(0x01);
        var mask = data[0];

        var positionFrames = (long)_tapePositionFrames;
        var isPlaySpeed = _transport is TransportState.Playing or TransportState.Recording;

        // Timer-1 (CTL).
        if ((mask & 0x04) != 0)
        {
            return Frame(0x74, 0x00, EncodeBcdTimecode(positionFrames));
        }

        // LTC user bits.
        if ((mask & 0x10) != 0)
        {
            return _tapeOut
                ? Frame(0x74, 0x05, 0, 0, 0, 0)
                : Frame(0x74, 0x05, 0x01, 0x01, 0x26, 0x20);
        }

        // Best available timecode (LTC | VITC): LTC at play speed, VITC when the deck
        // has a VITC reader near play speed, corrected LTC otherwise.
        if ((mask & 0x03) != 0)
        {
            var tcFrames = positionFrames + TimecodeOffsetFrames;
            if (_tapeOut)
            {
                return Frame(0x74, 0x14, EncodeBcdTimecode(positionFrames));
            }
            if (isPlaySpeed)
            {
                return Frame(0x74, 0x04, EncodeBcdTimecode(tcFrames)); // LTC
            }
            if (_personality.SupportsVitc && _transport == TransportState.Stopped)
            {
                return Frame(0x74, 0x16, EncodeBcdTimecode(tcFrames)); // hold VITC
            }
            return Frame(0x74, 0x14, EncodeBcdTimecode(tcFrames)); // corrected LTC
        }

        return Nak(0x01);
    }

    private static byte[] EncodeBcdTimecode(long totalFrames)
    {
        if (totalFrames < 0) totalFrames = 0;
        var frames = (int)(totalFrames % FrameRate);
        var totalSeconds = totalFrames / FrameRate;
        var seconds = (int)(totalSeconds % 60);
        var minutes = (int)(totalSeconds / 60 % 60);
        var hours = (int)(totalSeconds / 3600 % 24);

        static byte Bcd(int value) => (byte)((value / 10) << 4 | value % 10);
        return new[] { Bcd(frames), Bcd(seconds), Bcd(minutes), Bcd(hours) };
    }

    private void AdvanceTape()
    {
        var now = Environment.TickCount64;
        var elapsedSeconds = (now - _lastAdvanceTicks) / 1000.0;
        _lastAdvanceTicks = now;

        var deltaFrames = elapsedSeconds * FrameRate;
        _tapePositionFrames += _transport switch
        {
            TransportState.Playing or TransportState.Recording => deltaFrames,
            TransportState.FastForwarding => deltaFrames * FastWindSpeed,
            TransportState.Rewinding => -deltaFrames * FastWindSpeed,
            TransportState.Jog or TransportState.Shuttle => deltaFrames * _variablePlayRate,
            _ => 0,
        };

        if (_tapePositionFrames < 0)
        {
            _tapePositionFrames = 0;
            if (_transport is TransportState.Rewinding or TransportState.Jog or TransportState.Shuttle)
            {
                _transport = TransportState.Stopped;
                _variablePlayRate = 0;
            }
        }
        if (_tapePositionFrames > TapeLengthFrames)
        {
            _tapePositionFrames = TapeLengthFrames;
            if (_transport is TransportState.Playing or TransportState.Recording
                or TransportState.FastForwarding or TransportState.Jog or TransportState.Shuttle)
            {
                _transport = TransportState.Stopped;
                _variablePlayRate = 0;
            }
        }
    }
}
