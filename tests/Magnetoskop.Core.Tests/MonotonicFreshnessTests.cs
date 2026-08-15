using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Tests;

public sealed class MonotonicFreshnessTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(2);

    [Fact]
    public void PreviewTransitionsFromFreshToStaleToLostAndRecovers()
    {
        const long firstFrame = 10 * TimeSpan.TicksPerSecond;

        Assert.Equal(ObservationFreshness.Fresh,
            MonotonicFreshness.Classify(firstFrame, firstFrame + TimeSpan.FromMilliseconds(499).Ticks,
                StaleAfter, LostAfter));
        Assert.Equal(ObservationFreshness.Stale,
            MonotonicFreshness.Classify(firstFrame, firstFrame + TimeSpan.FromMilliseconds(501).Ticks,
                StaleAfter, LostAfter));
        Assert.Equal(ObservationFreshness.Lost,
            MonotonicFreshness.Classify(firstFrame, firstFrame + TimeSpan.FromSeconds(2.1).Ticks,
                StaleAfter, LostAfter));

        var recoveredFrame = firstFrame + TimeSpan.FromSeconds(2.1).Ticks;
        Assert.Equal(ObservationFreshness.Fresh,
            MonotonicFreshness.Classify(recoveredFrame, recoveredFrame,
                StaleAfter, LostAfter));
    }

    [Fact]
    public void MissingFrameTimestamp_IsUnavailable()
    {
        Assert.Equal(ObservationFreshness.Unavailable,
            MonotonicFreshness.Classify(null, 123, StaleAfter, LostAfter));
    }
}
