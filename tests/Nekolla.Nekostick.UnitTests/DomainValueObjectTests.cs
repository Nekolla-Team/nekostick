using System.Collections.Immutable;
using Nekolla.Nekostick.Domain;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class DomainValueObjectTests
{
    private static readonly Guid Version7Id =
        Guid.Parse("018f3a52-4cde-7abc-8def-0123456789ab");

    private static readonly Guid Version6Id =
        Guid.Parse("018f3a52-4cde-6abc-8def-0123456789ab");

    private static readonly Guid Version4Id =
        Guid.Parse("018f3a52-4cde-4abc-8def-0123456789ab");

    private static readonly Guid InvalidVariantVersion7Id =
        Guid.Parse("018f3a52-4cde-7abc-cdef-0123456789ab");

    [Fact]
    public void UuidV7ChecksVersionAndRejectsEmptyValues()
    {
        Assert.True(UuidV7.IsVersion7(Version7Id));
        Assert.False(UuidV7.IsVersion7(Version6Id));
        Assert.False(UuidV7.IsVersion7(Version4Id));
        Assert.False(UuidV7.IsVersion7(InvalidVariantVersion7Id));
        Assert.False(UuidV7.IsVersion7(Guid.Empty));
    }

    [Fact]
    public void EntityBaseCreatesUtcIdentityAndInitialVersion()
    {
        var localTime = new DateTimeOffset(
            2026,
            8,
            16,
            12,
            30,
            0,
            TimeSpan.FromHours(5));
        var entity = new ProbeEntity(
            new FixedUuidGenerator(),
            new FixedTimeProvider(localTime));

        Assert.Equal(Version7Id, entity.Id);
        Assert.Equal(localTime.ToUniversalTime(), entity.CreatedAt);
        Assert.Equal(localTime.ToUniversalTime(), entity.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, entity.CreatedAt.Offset);
        Assert.Equal(1L, entity.Version);
    }

    [Fact]
    public void EntityBaseTouchAdvancesVersionAndRejectsEarlierTime()
    {
        var createdAt = new DateTimeOffset(
            2026,
            8,
            16,
            7,
            0,
            0,
            TimeSpan.Zero);
        var entity = new ProbeEntity(Version7Id, createdAt, 4);
        var laterTime = createdAt.AddMinutes(2).ToOffset(TimeSpan.FromHours(-4));

        entity.TouchAt(laterTime);

        Assert.Equal(laterTime.ToUniversalTime(), entity.UpdatedAt);
        Assert.Equal(5L, entity.Version);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => entity.TouchAt(createdAt.AddMinutes(-1)));
    }

    [Fact]
    public void EntityBaseRejectsNonVersionSevenIdsAndNegativeVersions()
    {
        var createdAt = new DateTimeOffset(2026, 8, 16, 7, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(
            () => new ProbeEntity(Version6Id, createdAt, 0));
        Assert.Throws<ArgumentException>(
            () => new ProbeEntity(Version4Id, createdAt, 0));
        Assert.Throws<ArgumentException>(
            () => new ProbeEntity(InvalidVariantVersion7Id, createdAt, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProbeEntity(Version7Id, createdAt, -1));
    }

    [Fact]
    public void RouteValueObjectsPreserveValidatedSemanticValues()
    {
        var matcher = new RouteMatcher(
            RouteMatcherKind.PrefixCaseInsensitive,
            "/api",
            ImmutableArray.Create("example.test"),
            ImmutableArray.Create("GET", "POST"));
        var forwarding = new ForwardingOptions(ForwardingKind.Replace, "/v2/$1");
        var target = new MicroserviceRouteTarget(Version7Id);
        var route = new RouteDefinition(
            new FixedUuidGenerator(),
            matcher,
            target,
            forwarding,
            priority: 10,
            enabled: false,
            timeProvider: new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.Equal(RouteMatcherKind.PrefixCaseInsensitive, matcher.Kind);
        Assert.Equal("/api", matcher.Pattern);
        Assert.Equal(2, matcher.Methods.Length);
        Assert.Equal(ForwardingKind.Replace, forwarding.Kind);
        Assert.Equal("/v2/$1", forwarding.ReplaceTemplate);
        Assert.Equal(RouteTargetKind.Microservice, target.Kind);
        Assert.Equal(Version7Id, target.ServiceId);
        Assert.Equal(10, route.Priority);
        Assert.False(route.Enabled);
    }

    [Fact]
    public void RouteValueObjectsRejectUnsafeOrInconsistentInputs()
    {
        Assert.Throws<ArgumentException>(
            () => new RouteMatcher(RouteMatcherKind.Exact, "\n"));
        Assert.Throws<ArgumentException>(
            () => new RouteMatcher(
                RouteMatcherKind.Exact,
                "/api",
                ImmutableArray.Create("example\t.test")));
        Assert.Throws<ArgumentException>(
            () => new ForwardingOptions(ForwardingKind.Replace));
        Assert.Throws<ArgumentException>(
            () => new ForwardingOptions(ForwardingKind.Preserve, "/replacement"));
        Assert.Throws<ArgumentException>(() => new MicroserviceRouteTarget(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MicroserviceRouteTarget(Version4Id));
        Assert.Throws<ArgumentException>(() => new MicroserviceRouteTarget(InvalidVariantVersion7Id));
        Assert.Throws<ArgumentException>(() => new StaticFileRouteTarget("relative-root"));
        Assert.Throws<ArgumentException>(() => new ExtensionHandlerRouteTarget(" "));
    }

    [Fact]
    public void ServiceValueObjectsValidatePortsTimeoutsAndRuntimeCounts()
    {
        var endpoint = new LoopbackEndpoint(LoopbackAddressKind.IPv6, 443);
        var processHealth = new HealthCheckDefinition(
            ServiceHealthCheckKind.Process,
            TimeSpan.FromSeconds(3));
        var httpHealth = new HealthCheckDefinition(
            ServiceHealthCheckKind.Http,
            TimeSpan.FromSeconds(5),
            "/health");
        var runtime = new ServiceRuntimeState(
            ServiceLifecycleState.Running,
            ServiceHealthState.Healthy,
            2);

        Assert.Equal(LoopbackAddressKind.IPv6, endpoint.Address);
        Assert.Equal(443, endpoint.Port);
        Assert.Equal(ServiceHealthCheckKind.Process, processHealth.Kind);
        Assert.Null(processHealth.HttpPath);
        Assert.Equal("/health", httpHealth.HttpPath);
        Assert.Equal(ServiceLifecycleState.Running, runtime.Lifecycle);
        Assert.Equal(ServiceHealthState.Healthy, runtime.Health);
        Assert.Equal(2, runtime.RestartCount);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoopbackEndpoint(LoopbackAddressKind.IPv4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HealthCheckDefinition(ServiceHealthCheckKind.Process, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(
            () => new HealthCheckDefinition(ServiceHealthCheckKind.Http, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceRuntimeState(ServiceLifecycleState.Failed, ServiceHealthState.Unknown, -1));
    }

    [Fact]
    public void ServiceDefinitionKeepsImmutableConfigurationAndEntityState()
    {
        var root = Path.GetTempPath();
        var health = new HealthCheckDefinition(
            ServiceHealthCheckKind.Tcp,
            TimeSpan.FromSeconds(2));
        var definition = new ServiceDefinition(
            new FixedUuidGenerator(),
            Path.Combine(root, "service-bin"),
            root,
            ImmutableArray.Create("--mode", "test"),
            ImmutableDictionary<string, string>.Empty.Add("MODE", "test"),
            ServiceStartPolicy.Eager,
            ServiceRestartPolicy.OnFailure,
            health,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.Equal(Version7Id, definition.Id);
        Assert.Equal(2, definition.Arguments.Length);
        Assert.Equal("test", definition.Environment["MODE"]);
        Assert.Equal(ServiceStartPolicy.Eager, definition.StartPolicy);
        Assert.Equal(ServiceRestartPolicy.OnFailure, definition.RestartPolicy);
        Assert.Same(health, definition.HealthCheck);
        Assert.Equal(1L, definition.Version);

        Assert.Throws<ArgumentException>(() => new ServiceDefinition(
            new FixedUuidGenerator(),
            "relative-service",
            root,
            default,
            ImmutableDictionary<string, string>.Empty,
            ServiceStartPolicy.Eager,
            ServiceRestartPolicy.Never,
            health,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch)));
        Assert.Throws<ArgumentException>(() => new ServiceDefinition(
            new FixedUuidGenerator(),
            Path.Combine(root, "service-bin"),
            "relative-working-directory",
            default,
            ImmutableDictionary<string, string>.Empty,
            ServiceStartPolicy.Eager,
            ServiceRestartPolicy.Never,
            health,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void ExtensionValueObjectsCompareAndTrackLoadState()
    {
        var identifier = new ExtensionIdentifier("sample.extension");
        var version = new SemanticVersion(1, 2, 3);
        var extension = new ExtensionDefinition(
            new FixedUuidGenerator(),
            identifier,
            version,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal("sample.extension", identifier.Value);
        Assert.Equal("sample.extension", identifier.ToString());
        Assert.Equal("1.2.3", version.ToString());
        Assert.True(new SemanticVersion(1, 3, 0).CompareTo(version) > 0);
        Assert.True(new SemanticVersion(1, 2, 2).CompareTo(version) < 0);
        Assert.Equal(ExtensionLoadState.Discovered, extension.LoadState);

        extension.SetLoadState(
            ExtensionLoadState.Loaded,
            new DateTimeOffset(2026, 8, 16, 15, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(ExtensionLoadState.Loaded, extension.LoadState);
        Assert.Equal(2L, extension.Version);
        Assert.Equal(TimeSpan.Zero, extension.UpdatedAt.Offset);
    }

    [Fact]
    public void ExtensionValueObjectsRejectInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => new ExtensionIdentifier(" "));
        Assert.Throws<ArgumentException>(() => new ExtensionIdentifier("extension\nname"));
        Assert.Throws<ArgumentException>(() => new ExtensionIdentifier(new string('x', 129)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticVersion(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticVersion(0, 0, -1));
    }

    private sealed class FixedUuidGenerator : IUuidV7Generator
    {
        public Guid Create() => Version7Id;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _value;

        public FixedTimeProvider(DateTimeOffset value) => _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }

    private sealed class ProbeEntity : EntityBase
    {
        public ProbeEntity(IUuidV7Generator uuidGenerator, TimeProvider? timeProvider)
            : base(uuidGenerator, timeProvider)
        {
        }

        public ProbeEntity(Guid id, DateTimeOffset createdAt, long version)
            : base(id, createdAt, version)
        {
        }

        public void TouchAt(DateTimeOffset updatedAt) => Touch(updatedAt);
    }
}
