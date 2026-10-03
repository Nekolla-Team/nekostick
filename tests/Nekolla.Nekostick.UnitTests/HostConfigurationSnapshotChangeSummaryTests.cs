using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Xunit;

using ContractHeaderRewriteConfiguration = Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationSnapshotChangeSummaryTests
{
    private static readonly Guid UpdatedRouteId = Guid.Parse("01900000-0000-7000-8000-000000000601");
    private static readonly Guid RemovedRouteId = Guid.Parse("01900000-0000-7000-8000-000000000602");
    private static readonly Guid AddedRouteId = Guid.Parse("01900000-0000-7000-8000-000000000603");
    private static readonly Guid UpdatedServiceId = Guid.Parse("01900000-0000-7000-8000-000000000604");
    private static readonly Guid RemovedServiceId = Guid.Parse("01900000-0000-7000-8000-000000000605");
    private static readonly Guid AddedServiceId = Guid.Parse("01900000-0000-7000-8000-000000000606");

    [Fact]
    public void SummaryListsChangedCategoriesAndIdsWithoutConfigurationValues()
    {
        var previous = CreateChangedSnapshot(isCandidate: false);
        var current = CreateChangedSnapshot(isCandidate: true);

        var summary = HostConfigurationSnapshotChangeSummary.Create(previous, current);

        Assert.Contains("global-settings added=0 removed=0 updated=1[global]", summary.Text);
        Assert.Contains(
            $"routes added=1[{AddedRouteId:D}] removed=1[{RemovedRouteId:D}] updated=1[{UpdatedRouteId:D}]",
            summary.Text);
        Assert.Contains(
            $"services added=1[{AddedServiceId:D}] removed=1[{RemovedServiceId:D}] updated=1[{UpdatedServiceId:D}]",
            summary.Text);
        Assert.Contains(
            "extension-records added=1[extension.beta] removed=1[extension.removed] updated=1[extension.alpha]",
            summary.Text);
        Assert.Contains(
            "extension-settings added=1[extension.beta] removed=1[extension.removed] updated=1[extension.alpha]",
            summary.Text);
        Assert.DoesNotContain("old-route-secret", summary.Text);
        Assert.DoesNotContain("new-route-secret", summary.Text);
        Assert.DoesNotContain("old-environment-secret", summary.Text);
        Assert.DoesNotContain("new-environment-secret", summary.Text);
        Assert.DoesNotContain("old-settings-secret", summary.Text);
        Assert.DoesNotContain("new-settings-secret", summary.Text);
    }

    [Fact]
    public void EmptyDiffRendersNoneAndSuppressesOnlySameRevisionWithoutGenerationChange()
    {
        var previous = CreateStableSnapshot(17, "host:config-publisher");
        var sameRevision = CreateStableSnapshot(17, "host:config-publisher");
        var changes = HostConfigurationSnapshotChangeSummary.Create(previous, sameRevision);

        Assert.True(changes.IsEmpty);
        Assert.Equal("none", changes.Text);
        Assert.True(HostConfigurationPublicationSemantics.ShouldSuppressDuplicatePublication(
            previous,
            sameRevision,
            changes,
            hasDispatchGenerationChange: false,
            forcedReloadRequested: false));
        Assert.False(HostConfigurationPublicationSemantics.ShouldSuppressDuplicatePublication(
            previous,
            sameRevision,
            changes,
            hasDispatchGenerationChange: true,
            forcedReloadRequested: false));
        Assert.False(HostConfigurationPublicationSemantics.ShouldSuppressDuplicatePublication(
            previous,
            sameRevision,
            changes,
            hasDispatchGenerationChange: false,
            forcedReloadRequested: true));

        var writerChanged = CreateStableSnapshot(17, "host:extension-node-state");
        var writerChangeSummary = HostConfigurationSnapshotChangeSummary.Create(previous, writerChanged);
        Assert.Equal("none", writerChangeSummary.Text);
        Assert.False(HostConfigurationPublicationSemantics.ShouldSuppressDuplicatePublication(
            previous,
            writerChanged,
            writerChangeSummary,
            hasDispatchGenerationChange: false,
            forcedReloadRequested: false));
    }

    [Fact]
    public void DuplicateItemIdsAreComparedOnce()
    {
        var previousRoutes = ImmutableArray.Create(
            CreateRoute(UpdatedRouteId, 1, "previous-first"),
            CreateRoute(UpdatedRouteId, 2, "previous-duplicate"));
        var currentRoutes = ImmutableArray.Create(
            CreateRoute(UpdatedRouteId, 1, "current-first"),
            CreateRoute(UpdatedRouteId, 3, "current-duplicate"));
        var previous = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            previousRoutes,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var current = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            currentRoutes,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

        var summary = HostConfigurationSnapshotChangeSummary.Create(previous, current);

        Assert.True(summary.IsEmpty);
    }

    [Fact]
    public void NullWriterIdentityIsAValidSameRevisionValue()
    {
        var previous = CreateStableSnapshot(17, null);
        var current = CreateStableSnapshot(17, null);
        var changes = HostConfigurationSnapshotChangeSummary.Create(previous, current);

        Assert.Null(previous.CommittedBy);
        Assert.True(HostConfigurationPublicationSemantics.ShouldSuppressDuplicatePublication(
            previous,
            current,
            changes,
            hasDispatchGenerationChange: false,
            forcedReloadRequested: false));
    }

    private static HostConfigurationSnapshot CreateStableSnapshot(long version, string? committedBy) =>
        new(
            version,
            new GlobalSettingsConfiguration(),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty)
        {
            CommittedBy = committedBy
        };
    private static HostConfigurationSnapshot CreateChangedSnapshot(bool isCandidate)
    {
        var version = isCandidate ? 2 : 1;
        var changedRoute = CreateRoute(
            UpdatedRouteId,
            version,
            isCandidate ? "new-route-secret" : "old-route-secret");
        var otherRoute = CreateRoute(
            isCandidate ? AddedRouteId : RemovedRouteId,
            1,
            isCandidate ? "new-unlisted-route-secret" : "old-unlisted-route-secret");
        var changedService = CreateService(
            UpdatedServiceId,
            version,
            isCandidate ? "new-environment-secret" : "old-environment-secret");
        var otherService = CreateService(
            isCandidate ? AddedServiceId : RemovedServiceId,
            1,
            isCandidate ? "new-unlisted-environment-secret" : "old-unlisted-environment-secret");
        var changedRecord = CreateRecord("extension.alpha", version);
        var otherRecord = CreateRecord(isCandidate ? "extension.beta" : "extension.removed", 1);
        var changedSettings = CreateSettings(
            "extension.alpha",
            version,
            isCandidate ? "new-settings-secret" : "old-settings-secret");
        var otherSettings = CreateSettings(
            isCandidate ? "extension.beta" : "extension.removed",
            1,
            isCandidate ? "new-unlisted-settings-secret" : "old-unlisted-settings-secret");

        return new HostConfigurationSnapshot(
            isCandidate ? 12 : 11,
            new GlobalSettingsConfiguration(
                version: version,
                maxConcurrentRequests: isCandidate ? 64 : 32),
            ImmutableArray.Create(changedRoute, otherRoute),
            ImmutableArray.Create(changedService, otherService),
            ImmutableArray.Create(changedRecord, otherRecord),
            ImmutableArray.Create(changedSettings, otherSettings))
        {
            CommittedBy = "host:config-publisher"
        };
    }


    private static RouteConfiguration CreateRoute(Guid id, long version, string secret) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/summary", default, default),
            new MicroserviceRouteTargetConfiguration(UpdatedServiceId),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<ContractHeaderRewriteConfiguration>.Empty,
            ImmutableArray<ContractHeaderRewriteConfiguration>.Empty,
            $"{{\"token\":\"{secret}\"}}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);

    private static ServiceConfiguration CreateService(Guid id, long version, string secret) =>
        new(
            id,
            true,
            "/opt/nekostick-service",
            ImmutableArray<string>.Empty,
            "/opt/nekostick",
            ImmutableDictionary<string, string>.Empty.Add("TOKEN", secret),
            ServiceStartMode.Eager,
            ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);

    private static ExtensionRecordConfiguration CreateRecord(string extensionId, long version) =>
        new(
            extensionId,
            "1.0.0",
            ExtensionLoadState.Disabled,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version,
            $"sha256:{new string('a', 64)}");

    private static ExtensionSettingsConfiguration CreateSettings(
        string extensionId,
        long version,
        string secret) =>
        new(extensionId, 1, $"{{\"token\":\"{secret}\"}}", version);
}
