namespace Magnetoskop.Core.Abstractions;

/// <summary>Serial byte-stream settings. Defaults match the Sony 9-pin specification:
/// 38400 baud, 8 data bits, odd parity, 1 stop bit.</summary>
public sealed record SerialSettings
{
    public required string PortName { get; init; }
    public int BaudRate { get; init; } = 38400;
    public int DataBits { get; init; } = 8;
    public SerialParity Parity { get; init; } = SerialParity.Odd;
    public int StopBits { get; init; } = 1;
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
}

public enum SerialParity
{
    None,
    Odd,
    Even,
}

/// <summary>
/// Raw byte transport. Deliberately protocol-agnostic so the Sony 9-pin layer
/// can be tested against an in-memory fake.
/// </summary>
public interface ISerialTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the whole buffer.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Reads up to buffer.Length bytes; returns the number read (0 = closed).</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Discards any buffered incoming data.</summary>
    void DiscardInput();
}

/// <summary>Enumerates serial ports available on the machine.</summary>
public interface ISerialPortEnumerator
{
    IReadOnlyList<string> GetPortNames();
}