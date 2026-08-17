using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceEndpointLeaseTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000002");

    [Fact]
    public async Task PublisherRejectsInvalidAndExpiredLeasesAndResolverReturnsLoopback()
    {
        var now = DateTimeOffset.UtcNow;
        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish(new[]
        {
            new HostServiceEndpointLease(Guid.Empty, 23456, now.AddMinutes(1)),
            new HostServiceEndpointLease(ServiceId, 0, now.AddMinutes(1)),
            new HostServiceEndpointLease(Guid.NewGuid(), 23456, now.AddSeconds(-1)),
            new HostServiceEndpointLease(ServiceId, 23456, now.AddMinutes(1))
        });

        var result = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal("http://127.0.0.1:23456/", result.Endpoint!.BaseUri.ToString());
        Assert.Single(publisher.Current);
    }

    [Fact]
    public async Task ResolverRejectsExpiredLeaseFromAccessor()
    {
        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish(new[] { new HostServiceEndpointLease(ServiceId, 23456, DateTimeOffset.UtcNow.AddMilliseconds(-1)) });

        var result = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
    }
}
