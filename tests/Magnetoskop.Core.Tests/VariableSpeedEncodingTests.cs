using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Tests;

public class VariableSpeedEncodingTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(32, 0.1)]
    [InlineData(64, 1.0)]
    public void ToPlayRate_MatchesSonyFormula(byte n, double expected)
    {
        Assert.Equal(expected, VariableSpeedEncoding.ToPlayRate(n), precision: 6);
    }

    [Fact]
    public void ToPlayRate_ShuttleMaxIsNear50x()
    {
        var rate = VariableSpeedEncoding.ToPlayRate(118);
        Assert.InRange(rate, 48.0, 50.0);
    }

    [Fact]
    public void FromPlayRate_PlayIs64()
    {
        Assert.Equal(64, VariableSpeedEncoding.FromPlayRate(1.0, VariableSpeedMode.Jog));
        Assert.Equal(64, VariableSpeedEncoding.FromPlayRate(1.0, VariableSpeedMode.Shuttle));
    }

    [Fact]
    public void FromPlayRate_ClampsJogToPlay()
    {
        Assert.Equal(VariableSpeedEncoding.JogMax,
            VariableSpeedEncoding.FromPlayRate(10.0, VariableSpeedMode.Jog));
    }

    [Fact]
    public void FromPlayRate_ZeroIsStill()
    {
        Assert.Equal(VariableSpeedEncoding.Still,
            VariableSpeedEncoding.FromPlayRate(0, VariableSpeedMode.Shuttle));
    }

    [Fact]
    public void FromWheel_CenterIsStill()
    {
        var (forward, speed) = VariableSpeedEncoding.FromWheel(0, VariableSpeedMode.Shuttle);
        Assert.True(forward);
        Assert.Equal(VariableSpeedEncoding.Still, speed);
    }

    [Fact]
    public void FromWheel_FullRightIsNearMaxShuttle()
    {
        var (forward, speed) = VariableSpeedEncoding.FromWheel(1.0, VariableSpeedMode.Shuttle);
        Assert.True(forward);
        Assert.True(speed >= 110);
        Assert.InRange(VariableSpeedEncoding.ToPlayRate(speed), 48.0, 50.0);
    }

    [Fact]
    public void FromWheel_FullRight_RespectsDeckMaxShuttleRate()
    {
        var (forward, speed) = VariableSpeedEncoding.FromWheel(
            1.0, VariableSpeedMode.Shuttle, maxShuttleRate: 42);
        Assert.True(forward);
        Assert.InRange(VariableSpeedEncoding.ToPlayRate(speed), 41.0, 42.5);
    }

    [Fact]
    public void FromPlayRate_ClampsToDeckMaxShuttleRate()
    {
        var speed = VariableSpeedEncoding.FromPlayRate(
            50.0, VariableSpeedMode.Shuttle, maxShuttleRate: 42);
        Assert.InRange(VariableSpeedEncoding.ToPlayRate(speed), 41.0, 42.5);
    }

    [Fact]
    public void FromWheel_FullLeftIsReverse()
    {
        var (forward, speed) = VariableSpeedEncoding.FromWheel(-0.5, VariableSpeedMode.Jog);
        Assert.False(forward);
        Assert.True(speed > VariableSpeedEncoding.Still);
    }

    [Fact]
    public void ToTransportCommand_MapsModeAndDirection()
    {
        Assert.Equal(TransportCommand.JogForward,
            VariableSpeedEncoding.ToTransportCommand(VariableSpeedMode.Jog, true));
        Assert.Equal(TransportCommand.ShuttleReverse,
            VariableSpeedEncoding.ToTransportCommand(VariableSpeedMode.Shuttle, false));
    }
}
