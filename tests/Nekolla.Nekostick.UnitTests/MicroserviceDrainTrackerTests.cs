using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class MicroserviceDrainTrackerTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("01900000-0000-7000-8000-000000000901");

    [Fact]
    public async Task DrainWaitCompletesOnlyAfterEveryLeaseIsDisposed()
    {
        var tracker = new MicroserviceDrainTracker();
        var first = tracker.BeginTracking(ServiceId, 41001);
        var second = tracker.BeginTracking(ServiceId, 41001);

        var drain = tracker
            .WaitDrainedAsync(
                ServiceId,
                41001,
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken)
            .AsTask();

        Assert.False(drain.IsCompleted);
        first.Dispose();
        Assert.False(drain.IsCompleted);

        second.Dispose();
        await drain;
    }
    [Fact]
    public async Task CompletedDrainCanTrackAndDrainTheSameEndpointAgain()
    {
        var tracker = new MicroserviceDrainTracker();
        var first = tracker.BeginTracking(ServiceId, 41001);
        var firstDrain = tracker.WaitDrainedAsync(
            ServiceId,
            41001,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken).AsTask();

        first.Dispose();
        await firstDrain;
        Assert.Equal(0, tracker.TrackedSlotCount);

        var second = tracker.BeginTracking(ServiceId, 41001);
        var secondDrain = tracker.WaitDrainedAsync(
            ServiceId,
            41001,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(secondDrain.IsCompleted);
        second.Dispose();
        await secondDrain;
        Assert.Equal(0, tracker.TrackedSlotCount);
    }


    [Fact]
    public async Task DrainWaitCompletesImmediatelyForAnIdleEndpoint()
    {
        var tracker = new MicroserviceDrainTracker();

        await tracker.WaitDrainedAsync(
            ServiceId,
            41002,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, tracker.TrackedSlotCount);
    }

    [Fact]
    public async Task DrainTimeoutCompletesNormallyAndLeavesActiveLeaseTracked()
    {
        var tracker = new MicroserviceDrainTracker();
        var lease = tracker.BeginTracking(ServiceId, 41003);

        await tracker.WaitDrainedAsync(
            ServiceId,
            41003,
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, tracker.TrackedSlotCount);
        lease.Dispose();
        Assert.Equal(0, tracker.TrackedSlotCount);
    }

    [Fact]
    public async Task ConcurrentBeginAndDisposeDrainsConsistently()
    {
        var tracker = new MicroserviceDrainTracker();
        var anchor = tracker.BeginTracking(ServiceId, 41004);
        var drain = tracker
            .WaitDrainedAsync(
                ServiceId,
                41004,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken)
            .AsTask();

        var workers = Enumerable
            .Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    using var lease = tracker.BeginTracking(ServiceId, 41004);
                }
            }))
            .ToArray();

        await Task.WhenAll(workers);
        Assert.False(drain.IsCompleted);

        anchor.Dispose();
        await drain;
        Assert.Equal(0, tracker.TrackedSlotCount);
    }

    [Fact]
    public void DrainedSlotsAreRemovedInsteadOfRetainedPerEndpoint()
    {
        var tracker = new MicroserviceDrainTracker();

        for (var index = 0; index < 512; index++)
        {
            using var lease = tracker.BeginTracking(Guid.NewGuid(), 42000 + index);
        }

        Assert.Equal(0, tracker.TrackedSlotCount);
    }
}
