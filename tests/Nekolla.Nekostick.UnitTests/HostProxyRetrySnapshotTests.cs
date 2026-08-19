using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostProxyRetrySnapshotTests
{
    private static readonly Guid RouteId =
        Guid.Parse("01900000-0000-7000-8000-000000000671");

    [Fact]
    public void RouteWithoutRetryOverrideInheritsGlobalPolicyAtSnapshotBuild()
    {
        var global = CreatePolicy(maxRetries: 2, initialBackoffMilliseconds: 250);
        var snapshot = CreateSnapshot(global, RoutingTestData.CreateRoute(
            RouteId,
            RouteMatcherType.Exact,
            "/retry"));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(snapshot));

        var executable = holder.RoutingSnapshot!.ExecutableRoutes[RouteId];
        Assert.Null(executable.Configuration.ProxyRetries);
        Assert.Same(global, executable.RetryPolicy);
    }

    [Fact]
    public void RouteRetryOverrideReplacesGlobalPolicyAtSnapshotBuild()
    {
        var global = CreatePolicy(maxRetries: 1, initialBackoffMilliseconds: 200);
        var routeOverride = CreatePolicy(maxRetries: 4, initialBackoffMilliseconds: 375);
        var snapshot = CreateSnapshot(
            global,
            RoutingTestData.CreateRoute(
                RouteId,
                RouteMatcherType.Exact,
                "/retry",
                proxyRetries: routeOverride));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(snapshot));

        var executable = holder.RoutingSnapshot!.ExecutableRoutes[RouteId];
        Assert.Same(routeOverride, executable.Configuration.ProxyRetries);
        Assert.Same(routeOverride, executable.RetryPolicy);
        Assert.NotSame(global, executable.RetryPolicy);
    }

    [Fact]
    public void PersistedRouteRetryOverrideSurvivesSnapshotMappingAndBuild()
    {
        var global = CreatePolicy(maxRetries: 1, initialBackoffMilliseconds: 200);
        var routeOverride = CreatePolicy(maxRetries: 4, initialBackoffMilliseconds: 375);
        var snapshot = HostConfigurationSnapshotMapper.Map(
            new ConfigurationRevision { Version = 1 },
            CreateGlobalSettings(global),
            [CreateRouteEntity(routeOverride)],
            Array.Empty<Service>(),
            Array.Empty<ExtensionRecord>(),
            Array.Empty<ExtensionSetting>());

        var mappedRoute = Assert.Single(snapshot.Routes);
        Assert.Equal(routeOverride, mappedRoute.ProxyRetries);

        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));

        var executable = holder.RoutingSnapshot!.ExecutableRoutes[RouteId];
        Assert.Same(mappedRoute.ProxyRetries, executable.RetryPolicy);
        Assert.Equal(routeOverride, executable.RetryPolicy);
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        ProxyRetryConfiguration global,
        RouteConfiguration route) =>
        new(
            1,
            new GlobalSettingsConfiguration(version: 1, proxyRetries: global),
            ImmutableArray.Create(route),
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private static ProxyRetryConfiguration CreatePolicy(
        int maxRetries,
        int initialBackoffMilliseconds) =>
        new(
            maxRetries,
            TimeSpan.FromMilliseconds(initialBackoffMilliseconds),
            TimeSpan.FromMilliseconds(1800),
            retryOnConnectionFailure: true,
            retryOnUpstreamDisconnect: true);

    private static GlobalSettings CreateGlobalSettings(ProxyRetryConfiguration policy) =>
        new()
        {
            Version = 1,
            AutoPortRangeStart = 20000,
            AutoPortRangeEnd = 29999,
            MaxRequestBodyBytes = GlobalSettingsConfiguration.HardMaximumRequestBodyBytes,
            MaxRequestHeaderBytes = GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes,
            MaxConcurrentRequests = 1024,
            ConfigurationPollIntervalSeconds = 30,
            TrustedProxyCidrsJson = "[]",
            ConnectTimeoutMilliseconds = 10000,
            HttpActivityTimeoutMilliseconds = 30000,
            HttpTotalTimeoutMilliseconds = 100000,
            WebSocketIdleTimeoutMilliseconds = 120000,
            RequestReadTimeoutMilliseconds = 30000,
            ProxyMaxRetries = policy.MaxRetries,
            ProxyInitialRetryBackoffMilliseconds = checked((int)policy.InitialBackoff.TotalMilliseconds),
            ProxyMaximumRetryBackoffMilliseconds = checked((int)policy.MaximumBackoff.TotalMilliseconds),
            ProxyRetryOnConnectionFailure = policy.RetryOnConnectionFailure,
            ProxyRetryOnUpstreamDisconnect = policy.RetryOnUpstreamDisconnect
        };

    private static Route CreateRouteEntity(ProxyRetryConfiguration policy)
    {
        var rootPath = Path.GetTempPath();
        return new Route
        {
            Id = RouteId,
            Enabled = true,
            MatcherType = RouteMatcherKind.Exact,
            Pattern = "/retry",
            HostPatternsJson = "[]",
            MethodsJson = "[]",
            TargetType = RouteTargetKind.StaticFile,
            TargetId = rootPath,
            StaticRootPath = rootPath,
            Priority = 0,
            ForwardingMode = ForwardingKind.Preserve,
            RequestHeaderRewritesJson = "[]",
            ResponseHeaderRewritesJson = "[]",
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Version = 1,
            ProxyMaxRetries = policy.MaxRetries,
            ProxyInitialRetryBackoffMilliseconds = checked((int)policy.InitialBackoff.TotalMilliseconds),
            ProxyMaximumRetryBackoffMilliseconds = checked((int)policy.MaximumBackoff.TotalMilliseconds),
            ProxyRetryOnConnectionFailure = policy.RetryOnConnectionFailure,
            ProxyRetryOnUpstreamDisconnect = policy.RetryOnUpstreamDisconnect
        };
    }
}
