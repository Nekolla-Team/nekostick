using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Xunit;

using ContractHeaderRewriteConfiguration = Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostProxyTimeoutSnapshotTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("01900000-0000-7000-8000-000000000601");

    private static readonly Guid RouteId =
        Guid.Parse("01900000-0000-7000-8000-000000000602");

    [Fact]
    public void PublishedSnapshotCompilesTheGlobalTimeoutPolicyIntoItsMicroserviceRoute()
    {
        var configured = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromSeconds(3),
            httpActivityTimeout: TimeSpan.FromSeconds(7),
            httpTotalTimeout: TimeSpan.FromSeconds(19),
            webSocketIdleTimeout: TimeSpan.FromSeconds(23));
        var snapshot = CreateSnapshot(1, configured);
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(snapshot));

        var published = holder.RoutingSnapshot;
        Assert.NotNull(published);
        var executable = published!.ExecutableRoutes[RouteId];
        var policy = executable.TimeoutPolicy;

        Assert.Equal(configured.ConnectTimeout, policy.ConnectTimeout);
        Assert.Equal(configured.HttpActivityTimeout, policy.ActivityTimeout);
        Assert.Equal(configured.HttpTotalTimeout, policy.HttpTotalTimeout);
        Assert.Equal(configured.WebSocketIdleTimeout, policy.WebSocketIdleTimeout);
    }

    [Fact]
    public void MicroserviceRequestReceivesThePublishedImmutableTimeoutPolicy()
    {
        var snapshot = CreateSnapshot(
            1,
            new ProxyTimeoutConfiguration(
                connectTimeout: TimeSpan.FromSeconds(4),
                httpActivityTimeout: TimeSpan.FromSeconds(8),
                httpTotalTimeout: TimeSpan.FromSeconds(16),
                webSocketIdleTimeout: TimeSpan.FromSeconds(32)));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(snapshot));

        var published = holder.RoutingSnapshot!;
        var policy = published.ExecutableRoutes[RouteId].TimeoutPolicy;
        var request = new MicroserviceProxyRequest(ServiceId, "/", policy);

        Assert.Same(policy, request.TimeoutPolicy);
        Assert.Equal(policy.ConnectTimeout, request.TimeoutPolicy.ConnectTimeout);
        Assert.Equal(policy.ActivityTimeout, request.TimeoutPolicy.ActivityTimeout);
        Assert.Equal(policy.HttpTotalTimeout, request.TimeoutPolicy.HttpTotalTimeout);
        Assert.Equal(policy.WebSocketIdleTimeout, request.TimeoutPolicy.WebSocketIdleTimeout);
    }

    [Fact]
    public void InvalidCandidateSettingsAreRejectedWithoutReplacingThePreviousTimeoutSnapshot()
    {
        var previous = CreateSnapshot(
            1,
            new ProxyTimeoutConfiguration(httpTotalTimeout: TimeSpan.FromSeconds(18)));
        var invalidCandidate = CreateSnapshot(
            2,
            new ProxyTimeoutConfiguration(httpTotalTimeout: TimeSpan.FromSeconds(36)),
            configurationPollInterval: TimeSpan.FromMilliseconds(500));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(previous));
        Assert.False(holder.TryReplace(invalidCandidate));

        Assert.Same(previous, holder.Current);
        var currentPolicy = holder.RoutingSnapshot!.ExecutableRoutes[RouteId].TimeoutPolicy;
        Assert.Equal(TimeSpan.FromSeconds(18), currentPolicy.HttpTotalTimeout);
        Assert.Equal(1, holder.Current!.Version);
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        ProxyTimeoutConfiguration proxyTimeouts,
        TimeSpan? configurationPollInterval = null)
    {
        var route = new RouteConfiguration(
            RouteId,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/timeout", default, default),
            new MicroserviceRouteTargetConfiguration(ServiceId),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<ContractHeaderRewriteConfiguration>.Empty,
            ImmutableArray<ContractHeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);
        var service = new ServiceConfiguration(
            ServiceId,
            true,
            Path.Combine(Path.GetTempPath(), "nekostick-timeout-service"),
            ImmutableArray<string>.Empty,
            Path.GetTempPath(),
            ImmutableDictionary<string, string>.Empty,
            ServiceStartMode.Eager,
            ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);

        return new HostConfigurationSnapshot(
            version,
            new GlobalSettingsConfiguration(
                version: version,
                configurationPollInterval: configurationPollInterval ?? TimeSpan.FromSeconds(30),
                proxyTimeouts: proxyTimeouts),
            ImmutableArray.Create(route),
            ImmutableArray.Create(service),
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
    }
}
