using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRoutingSnapshotPublicationTests
{
    [Fact]
    public void ValidSnapshotPublishesConfigurationAndCompiledMatcherTogether()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(200),
            RouteMatcherType.Exact,
            "/published");
        var configuration = RoutingTestData.CreateSnapshot(
            1,
            ImmutableArray.Create(route));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(configuration));

        var routing = holder.RoutingSnapshot;
        Assert.NotNull(routing);
        Assert.Same(configuration, holder.Current);
        Assert.Same(configuration, routing!.Configuration);
        Assert.Equal(1, routing.Matcher.RouteCount);

        var result = routing.Matcher.Match(
            new RouteMatchInput("/published", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(route.Id, result.Match!.RouteId);
    }

    [Fact]
    public void InvalidRouteCompilationPreservesThePreviouslyPublishedPair()
    {
        var prior = RoutingTestData.CreateSnapshot(1, ImmutableArray<RouteConfiguration>.Empty);
        var invalidRoute = RoutingTestData.CreateRoute(
            RoutingTestData.Id(201),
            RouteMatcherType.Regex,
            "[");
        var invalidCandidate = RoutingTestData.CreateSnapshot(
            2,
            ImmutableArray.Create(invalidRoute));
        var build = RouteMatchSnapshotBuilder.Build(invalidCandidate.Routes);
        var holder = new HostConfigurationSnapshotHolder();

        Assert.False(build.IsSuccess);
        Assert.True(holder.TryReplace(prior));
        Assert.False(holder.TryReplace(invalidCandidate));

        Assert.Same(prior, holder.Current);
        Assert.Same(prior, holder.RoutingSnapshot!.Configuration);
        Assert.Equal(0, holder.RoutingSnapshot.Matcher.RouteCount);
    }
}
