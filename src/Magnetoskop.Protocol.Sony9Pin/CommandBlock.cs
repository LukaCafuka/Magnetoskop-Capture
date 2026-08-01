namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// A Sony 9-pin command block:
///   byte 0: CMD-1 (upper nibble) | DATA COUNT (lower nibble, max 15)
///   byte 1: CMD-2
///   bytes 2..n: DATA-1..DATA-N
///   last byte: CHECKSUM = lower 8 bits of the sum of all preceding bytes
/// Reference: context_source/sony_9pin (Rick Davies protocol summary).
/// </summary>
public sealed class CommandBlock
{
    public const int MaxDataLength = 15;

    public byte Cmd1 { get; }
    public byte Cmd2 { get; }
    public IReadOnlyList<byte> Data { get; }

    /// <summary>Upper nibble of the first byte (the command group).</summary>
    public byte CommandGroup => (byte)(Cmd1 >> 4);

    public CommandBlock(byte cmd1, byte cmd2, params byte[] data)
    {
        data ??= Array.Empty<byte>();
        if (data.Length > MaxDataLength)
        {
            throw new ArgumentException(
                $"Data count {data.Length} exceeds the protocol maximum of {MaxDataLength}.", nameof(data));
        }

        var declaredCount = cmd1 & 0x0F;
        if (declaredCount != 0 && declaredCount != data.Length)
        {
            throw new ArgumentException(
                $"CMD-1 declares {declaredCount} data bytes but {data.Length} were supplied.", nameof(cmd1));
        }

        // Normalize: always encode the actual data count in the low nibble.
        Cmd1 = (byte)((cmd1 & 0xF0) | data.Length);
        Cmd2 = cmd2;
        Data = data;
    }

    /// <summary>Computes the checksum: lower eight bits of the byte sum.</summary>
    public static byte ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)(sum & 0xFF);
    }

    /// <summary>Serializes the block including the trailing checksum.</summary>
    public byte[] ToBytes()
    {
        var result = new byte[2 + Data.Count + 1];
        result[0] = Cmd1;
        result[1] = Cmd2;
        for (var i = 0; i < Data.Count; i++) result[2 + i] = Data[i];
        result[^1] = ComputeChecksum(result.AsSpan(0, result.Length - 1));
        return result;
    }

    /// <summary>
    /// Attempts to parse a complete block from the start of <paramref name="buffer"/>.
    /// Returns null when more bytes are needed.
    /// </summary>
    /// <exception cref="ChecksumException">The trailing checksum does not match.</exception>
    public static CommandBlock? TryParse(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < 3) return null; // minimum: cmd1 + cmd2 + checksum

        var dataCount = buffer[0] & 0x0F;
        var totalLength = 2 + dataCount + 1;
        if (buffer.Length < totalLength) return null;

        var expected = ComputeChecksum(buffer[..(totalLength - 1)]);
        var actual = buffer[totalLength - 1];
        if (expected != actual)
        {
            throw new ChecksumException(
                $"Checksum mismatch: expected 0x{expected:X2}, received 0x{actual:X2}.");
        }

        var data = buffer.Slice(2, dataCount).ToArray();
        consumed = totalLength;
        return new CommandBlock(buffer[0], buffer[1], data);
    }

    public override string ToString()
        => Convert.ToHexString(ToBytes()).Replace("-", " ");
}

/// <summary>Thrown when a received block fails checksum validation.</summary>
public sealed class ChecksumException : IOException
{
    public ChecksumException(string message) : base(message) { }
}