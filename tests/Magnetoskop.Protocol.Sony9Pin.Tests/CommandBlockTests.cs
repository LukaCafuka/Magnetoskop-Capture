namespace Magnetoskop.Protocol.Sony9Pin.Tests;

public class CommandBlockTests
{
    [Fact]
    public void Play_SerializesWithCorrectChecksum()
    {
        // 20 01 -> checksum = 0x21
        var bytes = Sony9PinCommands.Play().ToBytes();
        Assert.Equal(new byte[] { 0x20, 0x01, 0x21 }, bytes);
    }

    [Fact]
    public void Stop_SerializesWithCorrectChecksum()
    {
        var bytes = Sony9PinCommands.Stop().ToBytes();
        Assert.Equal(new byte[] { 0x20, 0x00, 0x20 }, bytes);
    }

    [Fact]
    public void FastForward_SerializesWithCorrectChecksum()
    {
        var bytes = Sony9PinCommands.FastForward().ToBytes();
        Assert.Equal(new byte[] { 0x20, 0x10, 0x30 }, bytes);
    }

    [Fact]
    public void Rewind_SerializesWithCorrectChecksum()
    {
        var bytes = Sony9PinCommands.Rewind().ToBytes();
        Assert.Equal(new byte[] { 0x20, 0x20, 0x40 }, bytes);
    }

    [Fact]
    public void Eject_SerializesWithCorrectChecksum()
    {
        var bytes = Sony9PinCommands.Eject().ToBytes();
        Assert.Equal(new byte[] { 0x20, 0x0F, 0x2F }, bytes);
    }

    [Fact]
    public void DeviceTypeRequest_Serializes()
    {
        var bytes = Sony9PinCommands.DeviceTypeRequest().ToBytes();
        Assert.Equal(new byte[] { 0x00, 0x11, 0x11 }, bytes);
    }

    [Fact]
    public void StatusSense_EncodesStartAndCountInDataByte()
    {
        // 61 20 with DATA-1 = 0x0A (start 0, count 10)
        var bytes = Sony9PinCommands.StatusSense(0, 10).ToBytes();
        Assert.Equal(0x61, bytes[0]);
        Assert.Equal(0x20, bytes[1]);
        Assert.Equal(0x0A, bytes[2]);
        Assert.Equal((byte)((0x61 + 0x20 + 0x0A) & 0xFF), bytes[3]);
    }

    [Fact]
    public void CurrentTimeSense_EncodesRequestMask()
    {
        var bytes = Sony9PinCommands.CurrentTimeSense(TimeSenseRequest.BestTimecode).ToBytes();
        Assert.Equal(new byte[] { 0x61, 0x0C, 0x03, 0x70 }, bytes);
    }

    [Fact]
    public void DataCountNibble_ReflectsActualDataLength()
    {
        var block = new CommandBlock(0x20, 0x11, 0x40); // jog with 1 data byte
        Assert.Equal(0x21, block.Cmd1); // low nibble forced to 1
    }

    [Fact]
    public void Constructor_RejectsMoreThan15DataBytes()
    {
        Assert.Throws<ArgumentException>(() => new CommandBlock(0x20, 0x00, new byte[16]));
    }

    [Fact]
    public void Constructor_RejectsMismatchedDeclaredCount()
    {
        Assert.Throws<ArgumentException>(() => new CommandBlock(0x24, 0x31, 0x01, 0x02));
    }

    [Fact]
    public void TryParse_RoundTripsSerializedBlock()
    {
        var original = Sony9PinCommands.CueUpWithData(new Core.Models.Timecode(1, 2, 3, 4));
        var bytes = original.ToBytes();

        var parsed = CommandBlock.TryParse(bytes, out var consumed);

        Assert.NotNull(parsed);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(original.Cmd1, parsed.Cmd1);
        Assert.Equal(original.Cmd2, parsed.Cmd2);
        Assert.Equal(original.Data, parsed.Data);
    }

    [Fact]
    public void TryParse_ReturnsNullForPartialBlock()
    {
        var bytes = Sony9PinCommands.Play().ToBytes();
        var parsed = CommandBlock.TryParse(bytes.AsSpan(0, 2), out var consumed);
        Assert.Null(parsed);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_ThrowsOnBadChecksum()
    {
        var bytes = Sony9PinCommands.Play().ToBytes();
        bytes[^1] ^= 0xFF;
        Assert.Throws<ChecksumException>(() =>
        {
            var span = new ReadOnlySpan<byte>(bytes);
            CommandBlock.TryParse(span, out _);
        });
    }

    [Fact]
    public void ComputeChecksum_IsLowerEightBitsOfSum()
    {
        // 0xFF + 0xFF = 0x1FE -> 0xFE
        Assert.Equal(0xFE, CommandBlock.ComputeChecksum(new byte[] { 0xFF, 0xFF }));
    }
}