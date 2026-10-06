using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceEndpointLeaseTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000002");
    private static readonly Guid GenerationId =
        Guid.Parse("018f0000-0000-7000-8000-000000000005");

    [Fact]
    public async Task PublisherRejectsInvalidAndExpiredLeasesAndResolverReturnsLoopback()
    {
        var now = DateTimeOffset.UtcNow;
        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish(new[]
        {
            new HostServiceEndpointLease(Guid.Empty, GenerationId, 23456, now.AddMinutes(1)),
            new HostServiceEndpointLease(ServiceId, GenerationId, 0, now.AddMinutes(1)),
            new HostServiceEndpointLease(Guid.NewGuid(), GenerationId, 23456, now.AddSeconds(-1)),
            new HostServiceEndpointLease(ServiceId, Guid.NewGuid(), 23456, now.AddMinutes(1)),
            new HostServiceEndpointLease(ServiceId, GenerationId, 23456, now.AddMinutes(1))
        });

        var result = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal("http://127.0.0.1:23456/", result.Endpoint!.BaseUri.ToString());
        Assert.Single(publisher.Current);
        Assert.Equal(GenerationId, publisher.Current[ServiceId].GenerationId);
    }

    [Fact]
    public async Task ResolverRejectsExpiredLeaseFromAccessor()
    {
        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish(new[] { new HostServiceEndpointLease(ServiceId, GenerationId, 23456, DateTimeOffset.UtcNow.AddMilliseconds(-1)) });

        var result = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task ExtensionEndpointResolutionCarriesGenerationAndUsesNotFoundForMissingAndExpiredLeases()
    {
        const string extensionId = "fixture.extension";
        var expiredLease = new HostServiceEndpointLease(
            ServiceId,
            GenerationId,
            23456,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            extensionId);
        var activeServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000006");
        var activeLease = new HostServiceEndpointLease(
            activeServiceId,
            GenerationId,
            23457,
            DateTimeOffset.UtcNow.AddMinutes(1),
            extensionId);
        var accessor = new SnapshotAccessor(
            ImmutableDictionary<Guid, HostServiceEndpointLease>.Empty
                .Add(ServiceId, expiredLease)
                .Add(activeServiceId, activeLease));
        var facade = new ExtensionEndpointFacade(extensionId, accessor);
        var missingServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000003");

        var missing = await facade.ResolveAsync(
            missingServiceId,
            TestContext.Current.CancellationToken);
        var expired = await facade.ResolveAsync(
            ServiceId,
            TestContext.Current.CancellationToken);
        var active = await facade.ResolveAsync(
            activeServiceId,
            TestContext.Current.CancellationToken);

        Assert.Same(ExtensionEndpointResolutionResult.NotFound, missing);
        Assert.Same(ExtensionEndpointResolutionResult.NotFound, expired);
        Assert.False(missing.Succeeded);
        var success = Assert.IsType<ExtensionEndpointResolutionSuccessResult>(active);
        Assert.Equal(GenerationId, success.Lease.GenerationId);
        Assert.Equal(GenerationId, Assert.Single(facade.Current).GenerationId);
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
