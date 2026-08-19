using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.UnitTests;

internal static class RoutingTestData
{
    internal static RouteConfiguration CreateRoute(
        Guid id,
        RouteMatcherType matcherType,
        string pattern,
        int priority = 0,
        DateTimeOffset? createdAt = null,
        ImmutableArray<string> hostPatterns = default,
        ImmutableArray<string> methods = default,
        ForwardingConfiguration? forwarding = null,
        long version = 1,
        ProxyRetryConfiguration? proxyRetries = null) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(matcherType, pattern, hostPatterns, methods),
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            priority,
            forwarding ?? new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            createdAt ?? DateTimeOffset.UnixEpoch,
            createdAt ?? DateTimeOffset.UnixEpoch,
            version,
            proxyRetries: proxyRetries);

    internal static RouteMatchSnapshot Build(params RouteConfiguration[] routes)
    {
        var result = RouteMatchSnapshotBuilder.Build(routes);
        return result.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");
    }

    internal static HostConfigurationSnapshot CreateSnapshot(
        long version,
        ImmutableArray<RouteConfiguration> routes) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: version),
            routes,
            default,
            default,
            default);

    internal static Guid Id(int value) =>
        Guid.Parse($"01900000-0000-7000-8000-{value:X12}");
}
