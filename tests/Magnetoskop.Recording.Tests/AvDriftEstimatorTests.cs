using Magnetoskop.Recording;

namespace Magnetoskop.Recording.Tests;

public sealed class AvDriftEstimatorTests
{
    private const int SampleRate = 48_000;
    private const int FrameRate = 25;

    [Theory]
    [InlineData(50.0)]
    [InlineData(-50.0)]
    [InlineData(100.0)]
    [InlineData(-100.0)]
    [InlineData(500.0)]
    [InlineData(-500.0)]
    public void AcceleratedClockDrift_ConvergesAndChangesPcmCount(double driftPpm)
    {
        // Use the production defaults: 10 s warmup, 30 s regression window,
        // 1 s updates, +/-1000 ppm clamp and 50 ppm/s slew.
        var estimator = new AvDriftEstimator(FrameRate, SampleRate);
        var resampler = new AdaptivePcmResampler(
            SampleRate, channels: 1, bitsPerSample: 16,
            clampPpm: 1000, slewPpmPerSecond: 50);

        // Four virtual hours are advanced without wall-clock waits. Independent,
        // deterministic sub-millisecond timestamp jitter models callback scheduling
        // noise while keeping each stream monotonic.
        AvDriftSnapshot snapshot = new();
        for (var second = 0; second <= 14_400; second++)
        {
            var host100ns = TimeSpan.FromSeconds(second).Ticks;
            var videoJitter100ns = second == 0
                ? 0
                : ((second * 17L) % 11 - 5) * TimeSpan.FromMilliseconds(0.07).Ticks;
            var audioJitter100ns = second == 0
                ? 0
                : ((second * 29L) % 13 - 6) * TimeSpan.FromMilliseconds(0.06).Ticks;
            estimator.ObserveVideo(host100ns + videoJitter100ns, second * FrameRate);
            snapshot = estimator.ObserveAudio(host100ns + audioJitter100ns, (long)Math.Round(
                second * SampleRate * (1.0 + driftPpm / 1_000_000.0)));
            if (!snapshot.IsReady) continue;

            resampler.SetTargetCorrection(snapshot.RequiredCorrectionPpm, TimeSpan.FromSeconds(1));
            snapshot = estimator.ReportAppliedCorrection(resampler.AppliedCorrectionPpm);
        }

        Assert.True(snapshot.IsReady);
        Assert.InRange(snapshot.EstimatedDriftPpm,
            driftPpm - 5.0, driftPpm + 5.0);
        Assert.InRange(snapshot.AppliedCorrectionPpm,
            driftPpm - 10.0, driftPpm + 10.0);
        Assert.NotNull(snapshot.CurrentOffset);
        Assert.InRange(Math.Abs(snapshot.CurrentOffset!.Value.TotalMilliseconds), 0, 20);

        // Validate that WDL changes actual emitted PCM length in the required
        // direction and by the ratio represented by its *actual* slewed correction.
        var pcmSecond = new byte[SampleRate * sizeof(short)];
        long outputFrames = 0;
        double expectedFrames = 0;
        var countResampler = new AdaptivePcmResampler(
            SampleRate, channels: 1, bitsPerSample: 16,
            clampPpm: 1000, slewPpmPerSecond: 50);
        for (var second = 0; second < 120; second++)
        {
            var applied = countResampler.SetTargetCorrection(
                driftPpm, TimeSpan.FromSeconds(1));
            expectedFrames += SampleRate / (1.0 + applied / 1_000_000.0);
            outputFrames += countResampler.Process(pcmSecond).Length / sizeof(short);
        }
        outputFrames += countResampler.Flush().Length / sizeof(short);

        Assert.InRange(outputFrames,
            (long)Math.Floor(expectedFrames) - 128,
            (long)Math.Ceiling(expectedFrames) + 128);
        if (driftPpm > 0)
            Assert.True(outputFrames < 120L * SampleRate);
        else
            Assert.True(outputFrames > 120L * SampleRate);
    }

    [Fact]
    public void PhaseError_PreservesRawCorrectionDemandBeyondClamp()
    {
        var estimator = new AvDriftEstimator(
            FrameRate,
            SampleRate,
            warmup: TimeSpan.FromSeconds(1),
            window: TimeSpan.FromSeconds(5),
            updateInterval: TimeSpan.FromMilliseconds(1),
            correctionClampPpm: 1000,
            slewPpmPerSecond: 50,
            phaseCorrectionTimeConstant: TimeSpan.FromSeconds(30));

        estimator.ObserveVideo(0, 0);
        estimator.ObserveAudio(0, 0);
        AvDriftSnapshot snapshot = new();
        for (var second = 1; second <= 20; second++)
        {
            estimator.ObserveVideo(TimeSpan.FromSeconds(second).Ticks, second * FrameRate);
            // The audio sample clock has the correct rate, but its observation is now
            // 100 ms late. Once the initial point leaves the regression window this is
            // a pure phase error, requiring about -3333 ppm over the 30 s phase horizon.
            snapshot = estimator.ObserveAudio(
                TimeSpan.FromSeconds(second).Ticks + TimeSpan.FromMilliseconds(100).Ticks,
                second * SampleRate);
        }

        Assert.True(snapshot.IsReady);
        Assert.InRange(Math.Abs(snapshot.EstimatedDriftPpm), 0, 2);
        Assert.True(Math.Abs(snapshot.RequiredCorrectionPpm) > 1000,
            $"Raw phase correction was unexpectedly hidden by the clamp: " +
            $"{snapshot.RequiredCorrectionPpm:F1} ppm.");

        var resampler = new AdaptivePcmResampler(
            SampleRate, channels: 1, bitsPerSample: 16,
            clampPpm: 1000, slewPpmPerSecond: 50);
        resampler.SetTargetCorrection(snapshot.RequiredCorrectionPpm, TimeSpan.FromMinutes(1));
        Assert.Equal(-1000, resampler.AppliedCorrectionPpm, precision: 6);
    }
}
