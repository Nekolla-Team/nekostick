using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationSnapshotTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("018f3a52-4cde-7abc-8def-0123456789ab");

    private static readonly Guid RouteId =
        Guid.Parse("018f3a53-4cde-7abc-8def-0123456789ab");

    private static readonly Guid StaticRouteId =
        Guid.Parse("018f3a54-4cde-7abc-8def-0123456789ab");

    private static readonly Guid ExtensionRouteId =
        Guid.Parse("018f3a55-4cde-7abc-8def-0123456789ab");

    private static readonly Guid UnknownServiceId =
        Guid.Parse("018f3a56-4cde-7abc-8def-0123456789ab");

    private static readonly Guid UnknownRouteId =
        Guid.Parse("018f3a57-4cde-7abc-8def-0123456789ab");

    [Fact]
    public void HolderPublishesOneCompleteImmutableSnapshotReference()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var snapshot = CreateCompleteSnapshot(4);

        Assert.True(holder.TryReplace(snapshot));
        Assert.True(holder.HasSnapshot);
        Assert.Same(snapshot, holder.Current);
        Assert.Same(snapshot, holder.Snapshot);
        Assert.Single(snapshot.Services);
        Assert.Equal(3, snapshot.Routes.Length);
        Assert.Single(snapshot.ExtensionRecords);
        Assert.Single(snapshot.ExtensionSettings);
    }

    [Fact]
    public void HolderRejectsAnOlderSnapshotAndKeepsTheLatestPublishedVersion()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var current = CreateCompleteSnapshot(5);
        var older = CreateCompleteSnapshot(4);

        Assert.True(holder.TryReplace(current));
        Assert.False(holder.TryReplace(older));

        Assert.Same(current, holder.Current);
        Assert.Equal(5L, holder.Current!.Version);
    }

    [Fact]
    public void HolderKeepsThePreviousSnapshotWhenCandidateValidationFails()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var current = CreateCompleteSnapshot(7);
        var invalid = CreateSnapshot(
            8,
            routes: ImmutableArray.Create(
                CreateRoute(
                    UnknownRouteId,
                    new MicroserviceRouteTargetConfiguration(UnknownServiceId),
                    8)));

        Assert.True(holder.TryReplace(current));
        Assert.False(holder.TryReplace(invalid));

        Assert.Same(current, holder.Current);
        Assert.Equal(7L, holder.Current!.Version);
        Assert.Equal(ServiceId, ((MicroserviceRouteTargetConfiguration)
            holder.Current.Routes[0].Target).ServiceId);
    }

    [Fact]
    public void ValidatorAcceptsAllSupportedRouteTargetsAndKnownExtensionReferences()
    {
        var snapshot = CreateCompleteSnapshot(3);

        Assert.True(HostConfigurationSnapshotValidator.IsComplete(snapshot));
    }

    [Fact]
    public void ValidatorRejectsUnknownServiceExtensionAndSettingsReferences()
    {
        var unknownServiceRoute = CreateSnapshot(
            1,
            routes: ImmutableArray.Create(
                CreateRoute(
                    UnknownRouteId,
                    new MicroserviceRouteTargetConfiguration(UnknownServiceId),
                    1)));
        var unknownExtensionRoute = CreateSnapshot(
            1,
            routes: ImmutableArray.Create(
                CreateRoute(
                    UnknownRouteId,
                    new ExtensionHandlerRouteTargetConfiguration("missing.extension"),
                    1)));
        var unknownExtensionSettings = CreateSnapshot(
            1,
            extensionSettings: ImmutableArray.Create(
                new ExtensionSettingsConfiguration("missing.extension", 0, "{}", 1)));

        Assert.False(HostConfigurationSnapshotValidator.IsComplete(unknownServiceRoute));
        Assert.False(HostConfigurationSnapshotValidator.IsComplete(unknownExtensionRoute));
        Assert.False(HostConfigurationSnapshotValidator.IsComplete(unknownExtensionSettings));
    }

    [Fact]
    public void ValidatorRejectsDuplicateRouteServiceAndExtensionIdentities()
    {
        var duplicateRoutes = CreateSnapshot(
            1,
            routes: ImmutableArray.Create(
                CreateRoute(RouteId, new StaticFileRouteTargetConfiguration(Path.GetTempPath()), 1),
                CreateRoute(RouteId, new StaticFileRouteTargetConfiguration(Path.GetTempPath()), 1)));
        var duplicateServices = CreateSnapshot(
            1,
            services: ImmutableArray.Create(CreateService(ServiceId, 1), CreateService(ServiceId, 1)));
        var duplicateExtensions = CreateSnapshot(
            1,
            extensionRecords: ImmutableArray.Create(
                CreateExtensionRecord("sample.extension", 1),
                CreateExtensionRecord("sample.extension", 1)));

        Assert.False(HostConfigurationSnapshotValidator.IsComplete(duplicateRoutes));
        Assert.False(HostConfigurationSnapshotValidator.IsComplete(duplicateServices));
        Assert.False(HostConfigurationSnapshotValidator.IsComplete(duplicateExtensions));
    }

    [Fact]
    public void ConfigurationBoundaryRejectsNegativeVersionsThatCannotBePublished()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExtensionSettingsConfiguration("sample.extension", 0, "{}", -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HostConfigurationSnapshot(
                -1,
                new GlobalSettingsConfiguration(),
                default,
                default,
                default,
                default));
    }


    [Fact]
    public void HolderStagesValidatedSnapshotWithoutPublishingCurrentOrRoutingSnapshot()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var candidate = CreateCompleteSnapshot(4);

        Assert.True(holder.TryStage(candidate));

        Assert.True(holder.HasSnapshot);
        Assert.Null(holder.Current);
        Assert.Null(holder.RoutingSnapshot);
    }

    [Fact]
    public void HolderRejectsOlderStagedVersionAndClearsOnlyMatchingCandidate()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var staged = CreateCompleteSnapshot(5);
        var older = CreateCompleteSnapshot(4);
        var newer = CreateCompleteSnapshot(6);

        Assert.True(holder.TryStage(staged));
        Assert.False(holder.TryStage(older));
        Assert.True(holder.TryStage(newer));
        Assert.True(holder.HasSnapshot);

        holder.ClearStaged(staged);
        Assert.True(holder.HasSnapshot);
        Assert.False(holder.TryStage(older));

        holder.ClearStaged(newer);
        Assert.False(holder.HasSnapshot);
    }

    [Fact]
    public async Task HolderDisposalClearsPublishedAndStagedSnapshots()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var published = CreateCompleteSnapshot(1);
        var staged = CreateCompleteSnapshot(2);

        Assert.True(holder.TryReplace(published));
        Assert.True(holder.TryStage(staged));

        await holder.DisposeAsync();

        Assert.False(holder.HasSnapshot);
        Assert.Null(holder.Current);
        Assert.Null(holder.RoutingSnapshot);
    }

    private static HostConfigurationSnapshot CreateCompleteSnapshot(long version) =>
        CreateSnapshot(
            version,
            routes: ImmutableArray.Create(
                CreateRoute(
                    RouteId,
                    new MicroserviceRouteTargetConfiguration(ServiceId),
                    version),
                CreateRoute(
                    StaticRouteId,
                    new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
                    version),
                CreateRoute(
                    ExtensionRouteId,
                    new ExtensionHandlerRouteTargetConfiguration("sample.extension"),
                    version)),
            services: ImmutableArray.Create(CreateService(ServiceId, version)),
            extensionRecords: ImmutableArray.Create(CreateExtensionRecord("sample.extension", version)),
            extensionSettings: ImmutableArray.Create(
                new ExtensionSettingsConfiguration("sample.extension", 0, "{}", version)));

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        ImmutableArray<RouteConfiguration> routes = default,
        ImmutableArray<ServiceConfiguration> services = default,
        ImmutableArray<ExtensionRecordConfiguration> extensionRecords = default,
        ImmutableArray<ExtensionSettingsConfiguration> extensionSettings = default) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: version),
            routes,
            services,
            extensionRecords,
            extensionSettings);

    private static RouteConfiguration CreateRoute(
        Guid id,
        RouteTargetConfiguration target,
        long version) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Prefix, "/api", default, default),
            target,
            0,
            new ForwardingConfiguration(ForwardingMode.Strip, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);

    private static ServiceConfiguration CreateService(Guid id, long version) =>
        new(
            id,
            true,
            Path.Combine(Path.GetTempPath(), "nekostick-unit-service"),
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

    private static ExtensionRecordConfiguration CreateExtensionRecord(
        string extensionId,
        long version) =>
        new(
            extensionId,
            "1.0.0",
            ExtensionLoadState.Loaded,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);
}
