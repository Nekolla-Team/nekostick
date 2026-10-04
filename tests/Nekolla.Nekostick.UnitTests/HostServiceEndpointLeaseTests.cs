using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
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

    [Fact]
    public async Task ExtensionEndpointResolutionUsesNotFoundForMissingAndExpiredLeases()
    {
        const string extensionId = "fixture.extension";
        var expiredLease = new HostServiceEndpointLease(
            ServiceId,
            23456,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            extensionId);
        var accessor = new SnapshotAccessor(
            ImmutableDictionary<Guid, HostServiceEndpointLease>.Empty.Add(ServiceId, expiredLease));
        var facade = new ExtensionEndpointFacade(extensionId, accessor);
        var missingServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000003");

        var missing = await facade.ResolveAsync(
            missingServiceId,
            TestContext.Current.CancellationToken);
        var expired = await facade.ResolveAsync(
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Same(ExtensionEndpointResolutionResult.NotFound, missing);
        Assert.Same(ExtensionEndpointResolutionResult.NotFound, expired);
        Assert.False(missing.Succeeded);
        var failure = Assert.IsType<ExtensionEndpointResolutionFailureResult>(expired);
        Assert.Equal(ExtensionEndpointResolutionFailureCode.NotFound, failure.Code);
        Assert.Equal("No active endpoint lease was found for the service.", failure.Detail.Message);
    }

    private sealed class SnapshotAccessor : IHostServiceEndpointSnapshotAccessor
    {
        internal SnapshotAccessor(ImmutableDictionary<Guid, HostServiceEndpointLease> current) =>
            Current = current;

        public ImmutableDictionary<Guid, HostServiceEndpointLease> Current { get; }
    }
}
