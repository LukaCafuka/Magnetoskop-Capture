namespace Magnetoskop.Core.Models;

/// <summary>Result of a J/K/L shuttle step for the VTR.</summary>
public enum JklActionKind
{
    Stop,
    Play,
    Shuttle,
}

/// <summary>Resolved transport action for a JKL step index.</summary>
public readonly record struct JklAction(JklActionKind Kind, bool Forward, double PlayRate);

/// <summary>
/// DaVinci Resolve-style J/K/L step table.
/// Signed step: 0 = stopped, +n = forward ladder, −n = reverse ladder.
/// </summary>
public static class JklShuttleSteps
{
    /// <summary>Forward rates (× play), index 0 = first L / K-from-stop.</summary>
    public static readonly double[] ForwardRates = [1, 2, 4, 8, 16, 32, 50];

    /// <summary>Reverse rates (× play), index 0 = first J (slow).</summary>
    public static readonly double[] ReverseRates = [0.25, 0.5, 1, 2, 4, 8, 16, 32, 50];

    public static int ApplyK(int step) => step != 0 ? 0 : 1;

    public static int ApplyL(int step)
        => step <= 0 ? 1 : Math.Min(step + 1, ForwardRates.Length);

    public static int ApplyJ(int step)
        => step >= 0 ? -1 : Math.Max(step - 1, -ReverseRates.Length);

    public static JklAction ToAction(int step)
    {
        if (step == 0)
        {
            return new JklAction(JklActionKind.Stop, Forward: true, PlayRate: 0);
        }

        if (step > 0)
        {
            var idx = Math.Clamp(step, 1, ForwardRates.Length) - 1;
            var rate = ForwardRates[idx];
            // 1× forward uses Play for normal servo lock.
            if (idx == 0)
            {
                return new JklAction(JklActionKind.Play, Forward: true, PlayRate: rate);
            }

            return new JklAction(JklActionKind.Shuttle, Forward: true, PlayRate: rate);
        }

        var revIdx = Math.Clamp(-step, 1, ReverseRates.Length) - 1;
        return new JklAction(JklActionKind.Shuttle, Forward: false, PlayRate: ReverseRates[revIdx]);
    }
}
