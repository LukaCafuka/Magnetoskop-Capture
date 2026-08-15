using Magnetoskop.App.Services;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.Tests;

public sealed class VtrObservationHistoryTests
{
    [Fact]
    public void FindNearestPreceding_NeverAssociatesFutureObservation()
    {
        var history = new VtrObservationHistory();
        var wall = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        history.Observe(
            new TimeInformation
            {
                Ltc = new Timecode(1, 0, 0, 0),
                LtcSource = TimecodeSource.Ltc,
                LtcReceivedAt = wall,
                LtcReceivedTimestamp100ns = 1_000,
            },
            PlayingStatus(wall), wall, 1_000);
        history.Observe(
            new TimeInformation
            {
                Ltc = new Timecode(1, 0, 0, 1),
                LtcSource = TimecodeSource.Ltc,
                LtcReceivedAt = wall.AddMilliseconds(40),
                LtcReceivedTimestamp100ns = 401_000,
            },
            PlayingStatus(wall.AddMilliseconds(40)), wall.AddMilliseconds(40), 401_000);

        var association = history.FindNearestPreceding(300_000);

        Assert.NotNull(association);
        Assert.Equal(new Timecode(1, 0, 0, 0), association!.Value);
    }

    [Fact]
    public void Association_TransitionsFreshToStaleToLost()
    {
        var history = new VtrObservationHistory();
        var wall = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        history.Observe(
            new TimeInformation
            {
                Vitc = new Timecode(2, 0, 0, 0),
                VitcSource = TimecodeSource.Vitc,
                VitcReceivedAt = wall,
                VitcReceivedTimestamp100ns = 10_000_000,
            },
            PlayingStatus(wall), wall, 10_000_000);

        var association = history.FindNearestPreceding(10_000_000)!;

        Assert.Equal(ObservationFreshness.Fresh,
            association.FreshnessAt(20_000_000));
        Assert.Equal(ObservationFreshness.Stale,
            association.FreshnessAt(20_000_001));
        Assert.Equal(ObservationFreshness.Lost,
            association.FreshnessAt(40_000_001));
    }

    [Fact]
    public void FindNearestPreceding_PrefersFreshTapeTimecodeOverNewerCtl()
    {
        var history = new VtrObservationHistory();
        var wall = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        history.Observe(
            new TimeInformation
            {
                Ltc = new Timecode(1, 0, 0, 0),
                LtcSource = TimecodeSource.Ltc,
                LtcReceivedAt = wall,
                LtcReceivedTimestamp100ns = 10_000_000,
                Ctl = new Timecode(0, 10, 0, 0),
                CtlSource = TimecodeSource.Ctl,
                CtlReceivedAt = wall.AddMilliseconds(20),
                CtlReceivedTimestamp100ns = 10_200_000,
            },
            PlayingStatus(wall.AddMilliseconds(20)),
            wall.AddMilliseconds(20),
            10_200_000);

        var association = history.FindNearestPreceding(10_400_000);

        Assert.NotNull(association);
        Assert.Equal(TimecodeSource.Ltc, association!.Source);
    }

    [Fact]
    public void FindNearestPreceding_UsesNewerCtlWhenTapeTimecodeIsStale()
    {
        var history = new VtrObservationHistory();
        var wall = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        history.Observe(
            new TimeInformation
            {
                Ltc = new Timecode(1, 0, 0, 0),
                LtcSource = TimecodeSource.Ltc,
                LtcReceivedAt = wall,
                LtcReceivedTimestamp100ns = 10_000_000,
                Ctl = new Timecode(0, 10, 0, 0),
                CtlSource = TimecodeSource.Ctl,
                CtlReceivedAt = wall.AddSeconds(1.5),
                CtlReceivedTimestamp100ns = 25_000_000,
            },
            PlayingStatus(wall.AddSeconds(1.5)),
            wall.AddSeconds(1.5),
            25_000_000);

        var association = history.FindNearestPreceding(25_100_000);

        Assert.NotNull(association);
        Assert.Equal(TimecodeSource.Ctl, association!.Source);
    }

    [Theory]
    [InlineData(TimecodeSource.Ltc, TransportState.Playing, true, false, true)]
    [InlineData(TimecodeSource.Vitc, TransportState.Playing, true, false, true)]
    [InlineData(TimecodeSource.CorrectedLtc, TransportState.Playing, true, false, false)]
    [InlineData(TimecodeSource.HoldVitc, TransportState.Playing, true, false, false)]
    [InlineData(TimecodeSource.Ltc, TransportState.Stopped, true, false, false)]
    [InlineData(TimecodeSource.Ltc, TransportState.Playing, false, false, false)]
    [InlineData(TimecodeSource.Ltc, TransportState.Playing, true, true, false)]
    public void Interpolation_RequiresUsableSourceForwardPlayAndFreshServo(
        TimecodeSource source,
        TransportState transport,
        bool servoLocked,
        bool tapeReverse,
        bool expected)
    {
        var association = new VtrFrameAssociation
        {
            Value = new Timecode(1, 2, 3, 4),
            Source = source,
            ReceivedTimestamp100ns = 10_000_000,
            ReceivedAt = DateTimeOffset.UtcNow,
            StatusTimestamp100ns = 10_000_000,
            Transport = transport,
            ServoLocked = servoLocked,
            TapeReverse = tapeReverse,
        };

        Assert.Equal(expected, association.CanInterpolateAt(10_500_000));
    }

    private static VtrStatus PlayingStatus(DateTimeOffset timestamp) => new()
    {
        IsConnected = true,
        Transport = TransportState.Playing,
        ServoLock = true,
        Timestamp = timestamp,
    };
}
