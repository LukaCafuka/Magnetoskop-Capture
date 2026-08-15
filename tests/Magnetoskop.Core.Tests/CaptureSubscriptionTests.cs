using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Core.Tests;

public sealed class CaptureSubscriptionTests
{
    [Fact]
    public async Task Preview_DropOldest_IsCountedExactlyAndKeepsNewestItems()
    {
        await using var subscription = new CaptureSubscription<int>(
            CaptureSubscriptionOptions.Preview(capacity: 2),
            singleWriter: true);

        Assert.IsAssignableFrom<ICaptureSubscription<int>>(subscription);

        Assert.True(subscription.TryPublish(1));
        Assert.True(subscription.TryPublish(2));
        Assert.True(subscription.TryPublish(3));

        var overflow = await subscription.FirstOverflow.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(CaptureOverflowKind.OldestItemDropped, overflow.Kind);
        Assert.Equal(CaptureConsumerRole.Preview, overflow.Role);
        Assert.Equal(1, overflow.OverflowCount);

        Assert.True(subscription.TryRead(out var first));
        Assert.True(subscription.TryRead(out var second));
        Assert.Equal(2, first);
        Assert.Equal(3, second);

        var metrics = subscription.Metrics;
        Assert.Equal(3, metrics.PublishAttempts);
        Assert.Equal(3, metrics.Enqueued);
        Assert.Equal(2, metrics.Dequeued);
        Assert.Equal(1, metrics.DroppedOldest);
        Assert.Equal(0, metrics.RejectedNew);
        Assert.Equal(0, metrics.QueueDepth);
        Assert.Equal(2, metrics.HighWatermark);
    }

    [Fact]
    public async Task Recording_RejectsNewWithoutReplacingAcceptedItems_AndSealsCleanly()
    {
        await using var subscription = new CaptureSubscription<int>(
            CaptureSubscriptionOptions.Recording(capacity: 2),
            singleWriter: true);

        Assert.True(subscription.TryPublish(10));
        Assert.True(subscription.TryPublish(20));
        Assert.False(subscription.TryPublish(30));

        var overflow = await subscription.FirstOverflow.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(CaptureOverflowKind.NewItemRejected, overflow.Kind);
        Assert.Equal(CaptureConsumerRole.Recording, overflow.Role);

        Assert.True(subscription.TryRead(out var first));
        Assert.True(subscription.TryRead(out var second));
        Assert.Equal(10, first);
        Assert.Equal(20, second);
        Assert.False(subscription.TryRead(out _));
        await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        // Admission stays sealed after the first rejection even though draining
        // created queue space; a post-gap item can never enter the recording.
        Assert.False(subscription.TryPublish(40));

        var metrics = subscription.Metrics;
        Assert.Equal(3, metrics.PublishAttempts);
        Assert.Equal(2, metrics.Enqueued);
        Assert.Equal(2, metrics.Dequeued);
        Assert.Equal(0, metrics.DroppedOldest);
        Assert.Equal(1, metrics.RejectedNew);
        Assert.Equal(0, metrics.QueueDepth);
        Assert.Equal(2, metrics.HighWatermark);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndInvokesRemovalExactlyOnce()
    {
        var removals = 0;
        var subscription = new CaptureSubscription<int>(
            CaptureSubscriptionOptions.Monitor(capacity: 1),
            singleWriter: true,
            onDisposed: _ => removals++);

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(1, removals);
        Assert.False(subscription.TryPublish(1));
    }

    [Fact]
    public void FrameAudit_UsesStrictRejectNewPolicy()
    {
        using var subscription = new CaptureSubscription<int>(
            CaptureSubscriptionOptions.FrameAudit(capacity: 1),
            singleWriter: true);

        Assert.Equal(CaptureOverflowPolicy.RejectNew, subscription.Options.OverflowPolicy);
        Assert.True(subscription.TryPublish(1));
        Assert.False(subscription.TryPublish(2));
        Assert.Equal(1, subscription.Metrics.RejectedNew);
        Assert.Equal(0, subscription.Metrics.DroppedOldest);
    }

    [Fact]
    public void DropOldest_MetricsRemainExactAcrossRepeatedEvictions()
    {
        using var subscription = new CaptureSubscription<int>(
            CaptureSubscriptionOptions.Preview(capacity: 3),
            singleWriter: true);

        for (var item = 0; item < 100; item++)
        {
            Assert.True(subscription.TryPublish(item));
        }

        var remaining = new List<int>();
        while (subscription.TryRead(out var item)) remaining.Add(item);
        Assert.Equal(new[] { 97, 98, 99 }, remaining);
        var metrics = subscription.Metrics;
        Assert.Equal(100, metrics.PublishAttempts);
        Assert.Equal(100, metrics.Enqueued);
        Assert.Equal(97, metrics.DroppedOldest);
        Assert.Equal(3, metrics.Dequeued);
        Assert.Equal(0, metrics.QueueDepth);
        Assert.Equal(3, metrics.HighWatermark);
    }

    [Fact]
    public async Task SimulatedCapture_StopCompletesSubscriptions_AndCanRestartCleanly()
    {
        await using var capture = new SimulatedVideoCaptureService(
            NullLogger<SimulatedVideoCaptureService>.Instance);
        var device = (await capture.EnumerateDevicesAsync()).Single();

        await capture.StartAsync(device);
        var firstSubscription = capture.Subscribe(CaptureSubscriptionOptions.Preview(capacity: 2));
        _ = await firstSubscription.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await capture.StopAsync();
        while (firstSubscription.TryRead(out _)) { }
        await firstSubscription.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(CaptureHealthState.Stopped, capture.Health.State);
        firstSubscription.Dispose();

        await capture.StartAsync(device);
        await using var secondSubscription = capture.Subscribe(
            CaptureSubscriptionOptions.Recording(capacity: 2));
        var frame = await secondSubscription.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, frame.DeliverySequence);
        Assert.True(frame.Timestamp100ns > 0);
        await capture.StopAsync();

        // A second stop must be harmless and must leave the service fully stopped.
        await capture.StopAsync();
        Assert.False(capture.IsCapturing);
        Assert.Equal(CaptureHealthState.Stopped, capture.Health.State);
    }
}
