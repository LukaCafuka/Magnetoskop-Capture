using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Protocol.Sony9Pin.Tests;

public class ControllerTests
{
    private static readonly byte[] AckBytes = new CommandBlock(0x10, 0x01).ToBytes();

    private static VtrDeviceProfile FastProfile(params TransportCommand[] supported) => new()
    {
        Id = "test",
        DisplayName = "Test deck",
        SupportedCommands = supported,
        StatusPollInterval = TimeSpan.FromMilliseconds(20),
        TimecodePollInterval = TimeSpan.FromMilliseconds(20),
        ResponseTimeout = TimeSpan.FromMilliseconds(100),
        MaxRetries = 1,
    };

    private static SerialSettings Settings => new() { PortName = "COM_FAKE" };

    /// <summary>Scripted deck: ACKs transport commands and answers senses like a stopped PVW.</summary>
    private static byte[]? DefaultResponder(CommandBlock cmd)
    {
        return (cmd.Cmd1 & 0xF0, cmd.Cmd2) switch
        {
            // Device type request -> PVW-2600 PAL
            (0x00, 0x11) => new CommandBlock(0x12, 0x11, 0x21, 0x40).ToBytes(),
            // Transport commands -> ACK
            (0x20, _) => AckBytes,
            // Preset / select (e.g. Timer Mode Select 41 36) -> ACK
            (0x40, _) => AckBytes,
            // Status sense -> stopped, servo ref present (7A 20 = 10 status bytes)
            (0x60, 0x20) => new CommandBlock(0x7A, 0x20,
                0x00, 0x20, 0x00, 0, 0, 0, 0, 0, 0x00, 0).ToBytes(),
            // Current time sense
            (0x60, 0x0C) when cmd.Data.Count > 0 => cmd.Data[0] switch
            {
                0x03 => new CommandBlock(0x74, 0x04, 0x10, 0x30, 0x20, 0x01).ToBytes(), // LTC 01:20:30:10
                0x04 => new CommandBlock(0x74, 0x00, 0x05, 0x00, 0x00, 0x00).ToBytes(), // CTL 00:00:00:05
                0x10 => new CommandBlock(0x74, 0x05, 0xAA, 0xBB, 0xCC, 0xDD).ToBytes(), // LTC UB
                _ => null,
            },
            _ => null,
        };
    }

    private static Sony9PinController CreateController(FakeSerialTransport transport, VtrDeviceProfile? profile = null)
        => new(transport, profile ?? FastProfile(), Settings, NullLogger<Sony9PinController>.Instance);

    [Fact]
    public async Task Connect_OpensPortWithSonySettings_AndIdentifiesDevice()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);

        await controller.ConnectAsync();

        Assert.True(controller.IsConnected);
        Assert.NotNull(transport.Settings);
        Assert.Equal(38400, transport.Settings!.BaudRate);
        Assert.Equal(SerialParity.Odd, transport.Settings.Parity);
        Assert.Equal(8, transport.Settings.DataBits);
        Assert.Contains("21 40", controller.DeviceDescription);
    }

    [Fact]
    public async Task Connect_SurvivesFailedDeviceTypeRequest()
    {
        var transport = new FakeSerialTransport
        {
            Responder = cmd => (cmd.Cmd1 & 0xF0, cmd.Cmd2) switch
            {
                (0x00, 0x11) => null, // no answer to identification
                _ => DefaultResponder(cmd),
            },
        };
        await using var controller = CreateController(transport);

        await controller.ConnectAsync();

        Assert.True(controller.IsConnected);
        Assert.Equal("Test deck", controller.DeviceDescription);
    }

    [Fact]
    public async Task Play_SendsCorrectBytes_AndSucceedsOnAck()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();

        await controller.SendTransportCommandAsync(TransportCommand.Play);

        Assert.Contains(transport.ReceivedCommands,
            c => (c.Cmd1 & 0xF0) == 0x20 && c.Cmd2 == 0x01);
    }

    [Fact]
    public async Task CueUp_SendsBcdTimecode()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();

        await controller.CueUpAsync(new Timecode(1, 0, 0, 0));

        var commands = transport.ReceivedCommands.ToArray();
        Assert.Contains(commands, c =>
            c.Cmd1 == 0x41 && c.Cmd2 == 0x36
            && c.Data.Count == 1 && c.Data[0] == 0x00);
        Assert.Contains(commands, c =>
            c.Cmd1 == 0x24 && c.Cmd2 == 0x31
            && c.Data.Count == 4
            && c.Data[0] == 0x00 // frames
            && c.Data[1] == 0x00 // seconds
            && c.Data[2] == 0x00 // minutes
            && c.Data[3] == 0x01); // hours
    }

    [Fact]
    public async Task Pause_SendsShuttleZero()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();

        await controller.SendTransportCommandAsync(TransportCommand.Pause);

        var commands = transport.ReceivedCommands.ToArray();
        Assert.Contains(commands, c =>
            c.Cmd1 == 0x21 && c.Cmd2 == 0x13
            && c.Data.Count == 1 && c.Data[0] == 0x00);
    }

    [Fact]
    public async Task ShuttleForward_SendsSpeedByte()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();

        await controller.SendVariableSpeedAsync(VariableSpeedMode.Shuttle, forward: true, speed: 0x40);

        Assert.Contains(transport.ReceivedCommands, c =>
            c.Cmd1 == 0x21 && c.Cmd2 == 0x13
            && c.Data.Count == 1 && c.Data[0] == 0x40);
    }

    [Fact]
    public async Task JogReverse_SendsSpeedByte()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();

        await controller.SendVariableSpeedAsync(VariableSpeedMode.Jog, forward: false, speed: 0x30);

        Assert.Contains(transport.ReceivedCommands, c =>
            c.Cmd1 == 0x21 && c.Cmd2 == 0x21
            && c.Data.Count == 1 && c.Data[0] == 0x30);
    }

    [Fact]
    public async Task UndefinedCommandNak_MapsToUnsupportedCommandException()
    {
        var nak = new CommandBlock(0x11, 0x12, (byte)NakError.UndefinedCommand).ToBytes();
        var transport = new FakeSerialTransport
        {
            Responder = cmd => (cmd.Cmd1 & 0xF0) == 0x20 ? nak : DefaultResponder(cmd),
        };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();

        await Assert.ThrowsAsync<UnsupportedCommandException>(
            () => controller.SendTransportCommandAsync(TransportCommand.Eject));
    }

    [Fact]
    public async Task ProfileRestriction_BlocksCommandWithoutTouchingWire()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        var profile = FastProfile(TransportCommand.Play, TransportCommand.Stop);
        await using var controller = CreateController(transport, profile);
        await controller.ConnectAsync();
        var writesBefore = transport.ReceivedCommands.Count;

        await Assert.ThrowsAsync<UnsupportedCommandException>(
            () => controller.SendTransportCommandAsync(TransportCommand.Eject));

        // No 20 0F must have been written (polls may continue).
        Assert.DoesNotContain(transport.ReceivedCommands.Skip(writesBefore),
            c => (c.Cmd1 & 0xF0) == 0x20 && c.Cmd2 == 0x0F);
    }

    [Fact]
    public async Task Command_WhenDisconnected_Throws()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);

        await Assert.ThrowsAsync<VtrCommunicationException>(
            () => controller.SendTransportCommandAsync(TransportCommand.Play));
    }

    [Fact]
    public async Task Polling_PublishesStatusAndTime()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);

        var statusTcs = new TaskCompletionSource<VtrStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeTcs = new TaskCompletionSource<TimeInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, s) => statusTcs.TrySetResult(s);
        controller.TimeChanged += (_, t) =>
        {
            if (t.Ltc is not null && t.Ctl is not null) timeTcs.TrySetResult(t);
        };

        await controller.ConnectAsync();

        var status = await AwaitWithTimeout(statusTcs.Task);
        Assert.Equal(TransportState.Stopped, status.Transport);

        var time = await AwaitWithTimeout(timeTcs.Task);
        Assert.Equal(new Timecode(1, 20, 30, 10), time.Ltc);
        Assert.Equal(new Timecode(0, 0, 0, 5), time.Ctl);
        Assert.Equal(TimecodeSource.Ltc, time.PrimarySource);
    }

    [Fact]
    public async Task Polling_PublishesUserBits()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);

        var ubTcs = new TaskCompletionSource<TimeInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.TimeChanged += (_, t) =>
        {
            if (t.LtcUserBits is not null) ubTcs.TrySetResult(t);
        };

        await controller.ConnectAsync();

        var time = await AwaitWithTimeout(ubTcs.Task, timeoutMs: 3000);
        Assert.Equal(new UserBits(0xAA, 0xBB, 0xCC, 0xDD), time.LtcUserBits);
    }

    [Fact]
    public async Task Disconnect_StopsPollingAndClosesPort()
    {
        var transport = new FakeSerialTransport { Responder = DefaultResponder };
        await using var controller = CreateController(transport);
        await controller.ConnectAsync();
        await Task.Delay(60); // let polling run

        await controller.DisconnectAsync();

        Assert.False(controller.IsConnected);
        Assert.False(transport.IsOpen);
        var countAfterDisconnect = transport.ReceivedCommands.Count;
        await Task.Delay(80);
        Assert.Equal(countAfterDisconnect, transport.ReceivedCommands.Count); // polling stopped
    }

    [Fact]
    public async Task Polling_SurvivesTransientFailures()
    {
        var failing = true;
        var transport = new FakeSerialTransport();
        transport.Responder = cmd =>
        {
            if (failing && (cmd.Cmd1 & 0xF0) == 0x60) return null; // sense requests time out
            return DefaultResponder(cmd);
        };
        var profile = FastProfile() with { ResponseTimeout = TimeSpan.FromMilliseconds(30) };
        await using var controller = CreateController(transport, profile);

        var statusTcs = new TaskCompletionSource<VtrStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, s) => statusTcs.TrySetResult(s);

        await controller.ConnectAsync();
        await Task.Delay(150); // several failed poll cycles
        failing = false;       // link recovers

        var status = await AwaitWithTimeout(statusTcs.Task, timeoutMs: 3000);
        Assert.Equal(TransportState.Stopped, status.Transport);
    }

    private static async Task<T> AwaitWithTimeout<T>(Task<T> task, int timeoutMs = 2000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(completed == task, $"Timed out after {timeoutMs} ms waiting for the expected event.");
        return await task;
    }
}