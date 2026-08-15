using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Tests;

public sealed class TimeInformationTests
{
    [Fact]
    public void IndependentReceiptTimes_DoNotRefreshOlderRegisters()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var time = new TimeInformation
        {
            Ltc = new Timecode(1, 0, 0, 0),
            LtcReceivedAt = now - TimeSpan.FromSeconds(2),
            Vitc = new Timecode(1, 0, 0, 1),
            VitcReceivedAt = now,
            Timestamp = now,
        };

        Assert.Equal(ObservationFreshness.Stale, time.LtcObservation!.Value.FreshnessAt(now));
        Assert.Equal(ObservationFreshness.Fresh, time.VitcObservation!.Value.FreshnessAt(now));
    }

    [Fact]
    public void GetPrimaryObservation_RejectsStalePrimaryAndUsesFreshFallback()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var time = new TimeInformation
        {
            Ltc = new Timecode(1, 0, 0, 0),
            LtcSource = TimecodeSource.CorrectedLtc,
            LtcReceivedAt = now - TimeSpan.FromSeconds(2),
            Vitc = new Timecode(1, 0, 0, 1),
            VitcSource = TimecodeSource.HoldVitc,
            VitcReceivedAt = now,
            PrimarySource = TimecodeSource.CorrectedLtc,
            Timestamp = now,
        };

        var observation = time.GetPrimaryObservation(now);

        Assert.NotNull(observation);
        Assert.Equal(new Timecode(1, 0, 0, 1), observation.Value.Value);
        Assert.Equal(TimecodeSource.HoldVitc, observation.Value.Source);
    }

    [Theory]
    [InlineData(0.5, ObservationFreshness.Fresh)]
    [InlineData(1.5, ObservationFreshness.Stale)]
    [InlineData(3.5, ObservationFreshness.Lost)]
    public void DefaultFreshnessThresholds_AreApplied(double ageSeconds, ObservationFreshness expected)
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected,
            TimeInformation.ClassifyFreshness(now - TimeSpan.FromSeconds(ageSeconds), now));
    }

    [Fact]
    public void MissingValue_IsUnavailableEvenWhenCompositeWasJustPublished()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var time = new TimeInformation { Timestamp = now };

        Assert.Null(time.LtcObservation);
        Assert.Null(time.GetPrimaryObservation(now));
    }

    [Fact]
    public void MonotonicFreshness_IsUnaffectedByWallClockJump()
    {
        var receivedAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        const long receivedTimestamp = 100 * TimeSpan.TicksPerSecond;
        var observation = new TimeObservation<Timecode>(
            new Timecode(1, 2, 3, 4),
            TimecodeSource.Ltc,
            receivedAt,
            receivedTimestamp);

        var wallClockAfterBackwardAdjustment = receivedAt - TimeSpan.FromHours(1);
        var monotonicNow = receivedTimestamp + TimeSpan.FromSeconds(1.5).Ticks;

        Assert.Equal(
            ObservationFreshness.Stale,
            observation.FreshnessAt(monotonicNow, wallClockAfterBackwardAdjustment));
        Assert.Equal(
            TimeSpan.FromSeconds(1.5),
            observation.AgeAt(monotonicNow, wallClockAfterBackwardAdjustment));
    }
}
