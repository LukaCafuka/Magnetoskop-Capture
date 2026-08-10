namespace Magnetoskop.Core.Models;

/// <summary>
/// Sony 9-pin variable-speed encoding (Jog / Shuttle DATA-1):
/// <c>TapeSpeed = 10^((N/32)-2)</c> × play. <c>N=0</c> is treated as still.
/// </summary>
public static class VariableSpeedEncoding
{
    public const byte Still = 0;
    /// <summary>Slowest useful moving speed (~0.03×).</summary>
    public const byte MinMoving = 16;
    /// <summary>1× play (N=64).</summary>
    public const byte Play = 64;
    /// <summary>Jog clamped near play speed.</summary>
    public const byte JogMax = 64;
    /// <summary>~50× play (DVR-2000 / generic shuttle max speed byte).</summary>
    public const byte ShuttleMax = 118;
    /// <summary>Default maximum shuttle rate (× play) when the deck profile does not override.</summary>
    public const double DefaultMaxShuttleRate = 50.0;

    public static double ToPlayRate(byte n)
    {
        if (n == Still) return 0;
        return Math.Pow(10.0, (n / 32.0) - 2.0);
    }

    /// <summary>Speed byte for a play-rate multiple, clamped to the protocol range.</summary>
    public static byte SpeedByteForRate(double rate)
    {
        if (rate <= 0) return Still;
        var n = (int)Math.Round(32.0 * (Math.Log10(rate) + 2.0));
        return (byte)Math.Clamp(n, MinMoving, ShuttleMax);
    }

    public static byte FromPlayRate(
        double rate, VariableSpeedMode mode, double maxShuttleRate = DefaultMaxShuttleRate)
    {
        if (rate <= 0) return Still;

        var maxRate = mode == VariableSpeedMode.Jog
            ? 1.0
            : Math.Clamp(maxShuttleRate, 1.0, DefaultMaxShuttleRate);
        var minRate = ToPlayRate(MinMoving);
        rate = Math.Clamp(rate, minRate, maxRate);

        var n = (int)Math.Round(32.0 * (Math.Log10(rate) + 2.0));
        var maxN = mode == VariableSpeedMode.Jog ? JogMax : SpeedByteForRate(maxRate);
        return (byte)Math.Clamp(n, MinMoving, maxN);
    }

    /// <summary>
    /// Maps a wheel deflection in [-1, +1] to direction + speed byte.
    /// Near-center snaps to still; magnitude uses a log curve for fine control near zero.
    /// </summary>
    public static (bool Forward, byte Speed) FromWheel(
        double position, VariableSpeedMode mode, double maxShuttleRate = DefaultMaxShuttleRate)
    {
        const double deadZone = 0.02;
        if (Math.Abs(position) < deadZone) return (true, Still);

        var forward = position >= 0;
        var abs = Math.Clamp(Math.Abs(position), 0, 1.0);
        abs = (abs - deadZone) / (1.0 - deadZone);

        var maxRate = mode == VariableSpeedMode.Jog
            ? 1.0
            : Math.Clamp(maxShuttleRate, 1.0, DefaultMaxShuttleRate);
        var minRate = ToPlayRate(MinMoving);
        var rate = minRate * Math.Pow(maxRate / minRate, abs);
        return (forward, FromPlayRate(rate, mode, maxShuttleRate));
    }

    public static TransportCommand ToTransportCommand(VariableSpeedMode mode, bool forward)
        => (mode, forward) switch
        {
            (VariableSpeedMode.Jog, true) => TransportCommand.JogForward,
            (VariableSpeedMode.Jog, false) => TransportCommand.JogReverse,
            (VariableSpeedMode.Shuttle, true) => TransportCommand.ShuttleForward,
            (VariableSpeedMode.Shuttle, false) => TransportCommand.ShuttleReverse,
            _ => TransportCommand.Stop,
        };
}
