using Magnetoskop.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Protocol.Sony9Pin.Tests;

public class TransceiverTests
{
    private static readonly byte[] AckBytes = new CommandBlock(0x10, 0x01).ToBytes();

    private static Sony9PinTransceiver CreateTransceiver(FakeSerialTransport transport, int retries = 2)
        => new(transport, NullLogger.Instance)
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(100),
            MaxRetries = retries,
            NakRetryDelay = TimeSpan.FromMilliseconds(1),
        };

    [Fact]
    public async Task Exchange_ReturnsAck()
    {
        var transport = new FakeSerialTransport { Responder = _ => AckBytes };
        var transceiver = CreateTransceiver(transport);

        var response = await transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None);

        Assert.IsType<Sony9PinResponse.Ack>(response);
        Assert.Single(transport.ReceivedCommands);
        Assert.Equal(0x01, transport.ReceivedCommands[0].Cmd2);
    }

    [Fact]
    public async Task Exchange_TimesOut_ThenThrowsAfterRetries()
    {
        var transport = new FakeSerialTransport { Responder = _ => null };
        var transceiver = CreateTransceiver(transport, retries: 2);

        var ex = await Assert.ThrowsAsync<VtrCommunicationException>(
            () => transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None));

        Assert.Equal(3, transport.WriteCount); // 1 initial + 2 retries
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Exchange_RecoversWhenRetrySucceeds()
    {
        var attempts = 0;
        var transport = new FakeSerialTransport();
        transport.Responder = _ => ++attempts < 2 ? null : AckBytes;
        var transceiver = CreateTransceiver(transport);

        var response = await transceiver.ExchangeAsync(Sony9PinCommands.Stop(), CancellationToken.None);

        Assert.IsType<Sony9PinResponse.Ack>(response);
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public async Task Exchange_RetriesOnTransmissionErrorNak()
    {
        var attempts = 0;
        var nakBytes = new CommandBlock(0x11, 0x12, (byte)NakError.ParityError).ToBytes();
        var transport = new FakeSerialTransport();
        transport.Responder = _ => ++attempts < 2 ? nakBytes : AckBytes;
        var transceiver = CreateTransceiver(transport);

        var response = await transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None);

        Assert.IsType<Sony9PinResponse.Ack>(response);
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public async Task Exchange_DoesNotRetryUndefinedCommandNak()
    {
        var nakBytes = new CommandBlock(0x11, 0x12, (byte)NakError.UndefinedCommand).ToBytes();
        var transport = new FakeSerialTransport { Responder = _ => nakBytes };
        var transceiver = CreateTransceiver(transport);

        var response = await transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None);

        var nak = Assert.IsType<Sony9PinResponse.Nak>(response);
        Assert.True(nak.IsUndefinedCommand);
        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task Exchange_RetriesOnCorruptResponse()
    {
        var attempts = 0;
        var corrupt = AckBytes.ToArray();
        corrupt[^1] ^= 0xFF; // break the checksum
        var transport = new FakeSerialTransport();
        transport.Responder = _ => ++attempts < 2 ? corrupt : AckBytes;
        var transceiver = CreateTransceiver(transport);

        var response = await transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None);

        Assert.IsType<Sony9PinResponse.Ack>(response);
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public async Task Exchange_HandlesResponseArrivingByteByByte()
    {
        var transport = new FakeSerialTransport();
        transport.Responder = _ =>
        {
            // Feed the ACK a byte at a time via the injection path.
            foreach (var b in AckBytes)
            {
                transport.InjectIncoming(b);
            }
            return null; // bytes already injected
        };
        // Cancel the "null response" retry mechanism by responding via injection only.
        var transceiver = CreateTransceiver(transport, retries: 0);

        var response = await transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None);
        Assert.IsType<Sony9PinResponse.Ack>(response);
    }

    [Fact]
    public async Task Exchange_CanBeCancelled()
    {
        var transport = new FakeSerialTransport { Responder = _ => null };
        var transceiver = new Sony9PinTransceiver(transport, NullLogger.Instance)
        {
            ResponseTimeout = TimeSpan.FromSeconds(30),
            MaxRetries = 0,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transceiver.ExchangeAsync(Sony9PinCommands.Play(), cts.Token));
    }

    [Fact]
    public async Task Exchange_SerializesConcurrentCommands()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var transport = new FakeSerialTransport();
        transport.Responder = _ =>
        {
            var current = Interlocked.Increment(ref inFlight);
            maxInFlight = Math.Max(maxInFlight, current);
            Thread.Sleep(10);
            Interlocked.Decrement(ref inFlight);
            return AckBytes;
        };
        var transceiver = CreateTransceiver(transport);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => transceiver.ExchangeAsync(Sony9PinCommands.Play(), CancellationToken.None));
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxInFlight); // never more than one outstanding command
        Assert.Equal(5, transport.ReceivedCommands.Count);
    }
}