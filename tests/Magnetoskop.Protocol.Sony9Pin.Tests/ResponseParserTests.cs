using Magnetoskop.Core.Models;

namespace Magnetoskop.Protocol.Sony9Pin.Tests;

public class ResponseParserTests
{
    private static Sony9PinResponse Classify(byte cmd1, byte cmd2, params byte[] data)
        => Sony9PinResponseParser.Classify(new CommandBlock(cmd1, cmd2, data));

    [Fact]
    public void Ack_IsClassified()
    {
        Assert.IsType<Sony9PinResponse.Ack>(Classify(0x10, 0x01));
    }

    [Fact]
    public void Nak_CarriesErrorBits()
    {
        var nak = Assert.IsType<Sony9PinResponse.Nak>(Classify(0x11, 0x12, (byte)NakError.ChecksumError));
        Assert.Equal(NakError.ChecksumError, nak.Error);
        Assert.False(nak.IsUndefinedCommand);
    }

    [Fact]
    public void Nak_UndefinedCommand_IsFlagged()
    {
        var nak = Assert.IsType<Sony9PinResponse.Nak>(Classify(0x11, 0x12, (byte)NakError.UndefinedCommand));
        Assert.True(nak.IsUndefinedCommand);
    }

    [Fact]
    public void DeviceType_ParsesTwoBytes()
    {
        // PVW-2600 PAL = 21 40 per the reference table
        var dt = Assert.IsType<Sony9PinResponse.DeviceType>(Classify(0x12, 0x11, 0x21, 0x40));
        Assert.Equal("21 40", dt.Code);
    }

    [Fact]
    public void StatusData_ReturnsRawBytes()
    {
        var status = Assert.IsType<Sony9PinResponse.StatusData>(
            Classify(0x74, 0x20, 0x00, 0x01, 0x80, 0x00));
        Assert.Equal(4, status.Bytes.Count);
        Assert.Equal(0x01, status.Bytes[1]);
    }

    [Theory]
    [InlineData(0x00, TimeDataKind.Timer1)]
    [InlineData(0x01, TimeDataKind.Timer2)]
    [InlineData(0x04, TimeDataKind.LtcTime)]
    [InlineData(0x06, TimeDataKind.VitcTime)]
    [InlineData(0x14, TimeDataKind.CorrectedLtcTime)]
    [InlineData(0x16, TimeDataKind.HoldVitcTime)]
    public void TimeData_KindsAreClassified(byte cmd2, TimeDataKind expected)
    {
        // Time 10:20:30:12
        var response = Classify(0x74, cmd2, 0x12, 0x30, 0x20, 0x10);
        var time = Assert.IsType<Sony9PinResponse.TimeData>(response);
        Assert.Equal(expected, time.Kind);
        Assert.Equal(new Timecode(10, 20, 30, 12), time.Timecode);
    }

    [Fact]
    public void Timer1_WrappedBelowZero_KeepsRawWrapInProtocol()
    {
        // Display layer applies signed formatting; protocol keeps deck wrap as-is.
        var response = Classify(0x74, 0x00, 0x24, 0x59, 0x59, 0x23);
        var time = Assert.IsType<Sony9PinResponse.TimeData>(response);
        Assert.Equal(TimeDataKind.Timer1, time.Kind);
        Assert.False(time.Timecode.IsNegative);
        Assert.Equal("23:59:59:24", time.Timecode.ToString());
    }

    [Fact]
    public void LtcTime_DoesNotReinterpretWrappedAsNegative()
    {
        var response = Classify(0x74, 0x04, 0x24, 0x59, 0x59, 0x23);
        var time = Assert.IsType<Sony9PinResponse.TimeData>(response);
        Assert.False(time.Timecode.IsNegative);
        Assert.Equal("23:59:59:24", time.Timecode.ToString());
    }

    [Theory]
    [InlineData(0x05, TimeDataKind.LtcUserBits)]
    [InlineData(0x07, TimeDataKind.VitcUserBits)]
    [InlineData(0x15, TimeDataKind.HoldLtcUserBits)]
    [InlineData(0x17, TimeDataKind.HoldVitcUserBits)]
    public void UserBits_KindsAreClassified(byte cmd2, TimeDataKind expected)
    {
        var response = Classify(0x74, cmd2, 0xDE, 0xAD, 0xBE, 0xEF);
        var ub = Assert.IsType<Sony9PinResponse.UserBitsData>(response);
        Assert.Equal(expected, ub.Kind);
        Assert.Equal(new UserBits(0xDE, 0xAD, 0xBE, 0xEF), ub.UserBits);
    }

    [Fact]
    public void UnknownResponse_KeepsRawBlock()
    {
        var response = Classify(0x70, 0xFE, 0x01);
        Assert.IsType<Sony9PinResponse.Unknown>(response);
        Assert.Equal(0xFE, response.Raw.Cmd2);
    }
}

public class StatusBitsParserTests
{
    [Fact]
    public void Play_StatusBits_ProduceMappedStatus()
    {
        // Data-1 bit0 = Play; Data-2 bit7 = Servo Lock
        var data = new byte[] { 0x00, 0x01, 0x80, 0, 0, 0, 0, 0, 0x00, 0 };
        var status = StatusBitsParser.Parse(data);
        Assert.Equal(TransportState.Playing, status.Transport);
        Assert.True(status.ServoLock);
        Assert.False(status.TapeOut);
    }

    [Fact]
    public void Stop_StatusBits()
    {
        var data = new byte[] { 0x00, 0x20 };
        var status = StatusBitsParser.Parse(data);
        Assert.Equal(TransportState.Stopped, status.Transport);
        Assert.False(status.Standby);
    }

    [Fact]
    public void StopWithStandbyBit_ReportsStandbyFlagIndependentOfTransport()
    {
        // Data-1 bit5 Stop + bit7 Standby (threaded stop / Standby On).
        var data = new byte[] { 0x00, 0xA0 };
        var status = StatusBitsParser.Parse(data);
        Assert.Equal(TransportState.Stopped, status.Transport);
        Assert.True(status.Standby);
    }

    [Fact]
    public void StandbyOnlyBit_ReportsTransportStandby()
    {
        var data = new byte[] { 0x00, 0x80 };
        var status = StatusBitsParser.Parse(data);
        Assert.Equal(TransportState.Standby, status.Transport);
        Assert.True(status.Standby);
    }

    [Fact]
    public void Record_TakesPriorityOverPlay()
    {
        // Record + Play often both set while recording
        var data = new byte[] { 0x00, 0x03 };
        Assert.Equal(TransportState.Recording, StatusBitsParser.Parse(data).Transport);
    }

    [Fact]
    public void FastForward_And_Rewind()
    {
        Assert.Equal(TransportState.FastForwarding,
            StatusBitsParser.Parse(new byte[] { 0x00, 0x04 }).Transport);
        Assert.Equal(TransportState.Rewinding,
            StatusBitsParser.Parse(new byte[] { 0x00, 0x08 }).Transport);
    }

    [Fact]
    public void Eject_IsDetected()
    {
        Assert.Equal(TransportState.Ejecting,
            StatusBitsParser.Parse(new byte[] { 0x00, 0x10 }).Transport);
    }

    [Fact]
    public void JogShuttleVarStill_FromDataByte2()
    {
        Assert.Equal(TransportState.Shuttle, StatusBitsParser.Parse(new byte[] { 0, 0, 0x20 }).Transport);
        Assert.Equal(TransportState.Jog, StatusBitsParser.Parse(new byte[] { 0, 0, 0x10 }).Transport);
        Assert.Equal(TransportState.Var, StatusBitsParser.Parse(new byte[] { 0, 0, 0x08 }).Transport);
        Assert.Equal(TransportState.Still, StatusBitsParser.Parse(new byte[] { 0, 0, 0x02 }).Transport);
    }

    [Fact]
    public void LocalAndCassetteOut_FromDataByte0()
    {
        var status = StatusBitsParser.Parse(new byte[] { 0x21 });
        Assert.True(status.IsLocal);
        Assert.True(status.TapeOut);
    }

    [Fact]
    public void AlarmBits_FromDataByte8()
    {
        var data = new byte[] { 0, 0x20, 0, 0, 0, 0, 0, 0, 0b0011_0111, 0 };
        var status = StatusBitsParser.Parse(data);
        Assert.True(status.RecordInhibited); // bit0
        Assert.True(status.SystemAlarm);     // bit1
        Assert.True(status.ServoAlarm);      // bit2
        Assert.True(status.EndOfTape);       // bit4
        Assert.True(status.NearEndOfTape);   // bit5
    }

    [Fact]
    public void ShortStatusData_IsToleratedWithDefaults()
    {
        // Some decks return fewer than 10 bytes; missing bytes read as 0.
        var status = StatusBitsParser.Parse(new byte[] { 0x00, 0x01 });
        Assert.Equal(TransportState.Playing, status.Transport);
        Assert.False(status.RecordInhibited);
    }
}