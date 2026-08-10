using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Tests;

public class JklShuttleStepsTests
{
    [Fact]
    public void ApplyK_TogglesStopAndPlayStep()
    {
        Assert.Equal(1, JklShuttleSteps.ApplyK(0));
        Assert.Equal(0, JklShuttleSteps.ApplyK(1));
        Assert.Equal(0, JklShuttleSteps.ApplyK(3));
        Assert.Equal(0, JklShuttleSteps.ApplyK(-2));
    }

    [Fact]
    public void ApplyL_StartsAtOneX_ThenAccelerates()
    {
        Assert.Equal(1, JklShuttleSteps.ApplyL(0));
        Assert.Equal(1, JklShuttleSteps.ApplyL(-3));
        Assert.Equal(2, JklShuttleSteps.ApplyL(1));
        Assert.Equal(JklShuttleSteps.ForwardRates.Length,
            JklShuttleSteps.ApplyL(JklShuttleSteps.ForwardRates.Length));
    }

    [Fact]
    public void ApplyJ_StartsSlowReverse_ThenAccelerates()
    {
        Assert.Equal(-1, JklShuttleSteps.ApplyJ(0));
        Assert.Equal(-1, JklShuttleSteps.ApplyJ(4));
        Assert.Equal(-2, JklShuttleSteps.ApplyJ(-1));
        Assert.Equal(-JklShuttleSteps.ReverseRates.Length,
            JklShuttleSteps.ApplyJ(-JklShuttleSteps.ReverseRates.Length));
    }

    [Fact]
    public void ToAction_MapsPlayStopAndShuttle()
    {
        Assert.Equal(JklActionKind.Stop, JklShuttleSteps.ToAction(0).Kind);
        Assert.Equal(JklActionKind.Play, JklShuttleSteps.ToAction(1).Kind);
        Assert.Equal(1.0, JklShuttleSteps.ToAction(1).PlayRate);

        var fast = JklShuttleSteps.ToAction(2);
        Assert.Equal(JklActionKind.Shuttle, fast.Kind);
        Assert.True(fast.Forward);
        Assert.Equal(2.0, fast.PlayRate);

        var slowRev = JklShuttleSteps.ToAction(-1);
        Assert.Equal(JklActionKind.Shuttle, slowRev.Kind);
        Assert.False(slowRev.Forward);
        Assert.Equal(0.25, slowRev.PlayRate);
    }

    [Fact]
    public void ToAction_TopStep_ClampsToDeckMaxShuttleRate()
    {
        var top = JklShuttleSteps.ToAction(JklShuttleSteps.ForwardRates.Length, maxShuttleRate: 42);
        Assert.Equal(JklActionKind.Shuttle, top.Kind);
        Assert.Equal(42.0, top.PlayRate);

        var topRev = JklShuttleSteps.ToAction(
            -JklShuttleSteps.ReverseRates.Length, maxShuttleRate: 42);
        Assert.Equal(42.0, topRev.PlayRate);
    }
}
