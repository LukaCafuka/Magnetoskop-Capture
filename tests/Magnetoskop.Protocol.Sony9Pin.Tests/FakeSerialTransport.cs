using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Protocol.Sony9Pin;

namespace Magnetoskop.Protocol.Sony9Pin.Tests;

/// <summary>
/// In-memory serial transport for protocol tests. A scripted responder
/// receives each written command block and decides what bytes come back.
/// </summary>
public sealed class FakeSerialTransport : ISerialTransport
{
    private readonly Channel<byte> _incoming = Channel.CreateUnbounded<byte>();
    private readonly List<byte> _written = new();

    /// <summary>Every complete command block the master has written.</summary>
    public List<CommandBlock> ReceivedCommands { get; } = new();

    /// <summary>Scripted response logic. Return null to simulate no response (timeout).</summary>
    public Func<CommandBlock, byte[]?>? Responder { get; set; }

    /// <summary>Number of writes observed (including retries).</summary>
    public int WriteCount { get; private set; }

    public bool IsOpen { get; private set; }

    public Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        IsOpen = true;
        return Task.CompletedTask;
    }

    public SerialSettings? Settings { get; private set; }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        IsOpen = false;
        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        _written.AddRange(buffer.ToArray());

        // Try to parse complete command blocks out of the written stream.
        while (true)
        {
            CommandBlock? block;
            int consumed;
            try
            {
                block = CommandBlock.TryParse(_written.ToArray(), out consumed);
            }
            catch (ChecksumException)
            {
                _written.Clear();
                break;
            }

            if (block is null) break;
            _written.RemoveRange(0, consumed);
            ReceivedCommands.Add(block);

            var response = Responder?.Invoke(block);
            if (response is not null)
            {
                foreach (var b in response)
                {
                    _incoming.Writer.TryWrite(b);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var b = await _incoming.Reader.ReadAsync(cancellationToken);
        buffer.Span[0] = b;
        var count = 1;
        while (count < buffer.Length && _incoming.Reader.TryRead(out var more))
        {
            buffer.Span[count++] = more;
        }
        return count;
    }

    public void DiscardInput()
    {
        while (_incoming.Reader.TryRead(out _)) { }
    }

    /// <summary>Injects raw bytes into the read stream (e.g., garbage or partial frames).</summary>
    public void InjectIncoming(params byte[] bytes)
    {
        foreach (var b in bytes) _incoming.Writer.TryWrite(b);
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}