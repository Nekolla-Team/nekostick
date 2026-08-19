using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Routing;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;
using ContractHealthCheckType = Nekolla.Nekostick.Contracts.ServiceHealthCheckType;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using ContractStartMode = Nekolla.Nekostick.Contracts.ServiceStartMode;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostDurableStoreOutageTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000031");

    [Fact]
    public async Task ExistingSnapshotRefusesNewLazyStartAfterDatabaseOutage()
    {
        var fixture = CreateFixture(ContractStartMode.Lazy, ContractRestartPolicy.Never);
        fixture.Runtime.MarkDatabaseUnavailable();

        var result = await fixture.Manager.EnsureReadyAsync(
            fixture.Snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, result.Status);
        Assert.Empty(fixture.Executor.StartedServices);
        Assert.Empty(fixture.LeaseStore.AcquireIntents);
        Assert.Same(fixture.Snapshot, fixture.Holder.Current);
        Assert.False(fixture.Runtime.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Degraded, fixture.Runtime.Status.Readiness);

        await fixture.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExistingServiceRemainsPublishedDuringOutageAndRefusesRestart()
    {
        var fixture = CreateFixture(ContractStartMode.Eager, ContractRestartPolicy.Always);
        var ready = await fixture.Manager.EnsureReadyAsync(
            fixture.Snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        Assert.Single(fixture.Executor.StartedServices);
        Assert.Single(fixture.Publisher.Current);

        fixture.Runtime.MarkDatabaseUnavailable();
        await fixture.Manager.RenewLeasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.LeaseStore.RenewCalls);
        Assert.Single(fixture.Publisher.Current);
        Assert.False(fixture.Runtime.NewLeasesAllowed);

        var stillReady = await fixture.Manager.EnsureReadyAsync(
            fixture.Snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, stillReady.Status);
        Assert.Single(fixture.Publisher.Current);

        await fixture.Manager.NotifyProcessExitAsync(ServiceId, successfulExit: false);

        Assert.Single(fixture.Executor.StartedServices);
        Assert.Single(fixture.LeaseStore.AcquireIntents);
        Assert.Equal(0, fixture.LeaseStore.RenewCalls);
        Assert.Empty(fixture.Publisher.Current);

        await fixture.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecoveryReopensOperationsOnlyAfterLatestSnapshotIsValidatedAndAccepted()
    {
        var fixture = CreateFixture(ContractStartMode.Lazy, ContractRestartPolicy.Never);
        fixture.Runtime.MarkDatabaseUnavailable();

        var invalidLatest = new HostConfigurationSnapshot(
            version: 2,
            globalSettings: fixture.Snapshot.GlobalSettings,
            routes: ImmutableArray<RouteConfiguration>.Empty,
            services: ImmutableArray.Create<ServiceConfiguration>((ServiceConfiguration)null!),
            extensionRecords: ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        Assert.False(fixture.Holder.TryReplace(invalidLatest));
        Assert.Same(fixture.Snapshot, fixture.Holder.Current);

        var blockedBeforeSync = await fixture.Manager.EnsureReadyAsync(
            fixture.Snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, blockedBeforeSync.Status);
        Assert.Empty(fixture.Executor.StartedServices);
        Assert.Empty(fixture.LeaseStore.AcquireIntents);

        var latest = CreateSnapshot(
            ContractStartMode.Lazy,
            ContractRestartPolicy.Never,
            snapshotVersion: 2,
            serviceVersion: 2);
        Assert.True(fixture.Holder.TryReplace(latest));
        Assert.Same(latest, fixture.Holder.Current);
        Assert.False(fixture.Runtime.NewServicesAllowed);

        var blockedBeforeAcceptance = await fixture.Manager.EnsureReadyAsync(
            latest,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, blockedBeforeAcceptance.Status);
        Assert.Empty(fixture.Executor.StartedServices);
        Assert.Empty(fixture.LeaseStore.AcquireIntents);

        fixture.Runtime.MarkSnapshotAccepted();
        Assert.True(fixture.Runtime.NewServicesAllowed);
        var ready = await fixture.Manager.EnsureReadyAsync(
            latest,
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        Assert.Single(fixture.Executor.StartedServices);
        Assert.Single(fixture.LeaseStore.AcquireIntents);

        await fixture.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupWithoutPublishedSnapshotRemainsUnreadyAndRefusesServiceStart()
    {
        var fixture = CreateFixture(
            ContractStartMode.Lazy,
            ContractRestartPolicy.Never,
            publishSnapshot: false,
            acceptSnapshot: false);

        Assert.Null(fixture.Holder.Current);
        Assert.Equal(HostReadinessState.Unready, fixture.Runtime.Status.Readiness);

        var result = await fixture.Manager.EnsureReadyAsync(
            fixture.Snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, result.Status);
        Assert.Empty(fixture.Executor.StartedServices);
        Assert.Empty(fixture.LeaseStore.AcquireIntents);

        await fixture.Manager.StopAsync(CancellationToken.None);
    }

    private static Fixture CreateFixture(
        ContractStartMode startMode,
        ContractRestartPolicy restartPolicy,
        long snapshotVersion = 1,
        long serviceVersion = 1,
        bool publishSnapshot = true,
        bool acceptSnapshot = true)
    {
        var snapshot = CreateSnapshot(startMode, restartPolicy, snapshotVersion, serviceVersion);
        var holder = new HostConfigurationSnapshotHolder();
        if (publishSnapshot)
        {
            Assert.True(holder.TryReplace(snapshot));
        }

        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        if (acceptSnapshot)
        {
            runtime.MarkSnapshotAccepted();
        }

        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = new HostServiceLifecycleManager(
            executor,
            new HealthyProbe(),
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false));
        return new Fixture(manager, snapshot, holder, runtime, executor, leaseStore, publisher);
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        ContractStartMode startMode,
        ContractRestartPolicy restartPolicy,
        long snapshotVersion = 1,
        long serviceVersion = 1)
    {
        var service = new ServiceConfiguration(
            ServiceId,
            enabled: true,
            fileName: "/tmp/nekostick-outage-fixture",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty,
            startMode: startMode,
            restartPolicy: restartPolicy,
            healthCheck: new ServiceHealthCheckConfiguration(
                ContractHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: serviceVersion);
        return new HostConfigurationSnapshot(
            version: snapshotVersion,
            globalSettings: new GlobalSettingsConfiguration(
                version: snapshotVersion,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35000,
                configurationPollInterval: TimeSpan.FromSeconds(1)),
            routes: ImmutableArray<RouteConfiguration>.Empty,
            services: ImmutableArray.Create(service),
            extensionRecords: ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray<ExtensionSettingsConfiguration>.Empty);
    }

    private sealed record Fixture(
        HostServiceLifecycleManager Manager,
        HostConfigurationSnapshot Snapshot,
        HostConfigurationSnapshotHolder Holder,
        HostRuntimeState Runtime,
        RecordingExecutor Executor,
        RecordingLeaseStore LeaseStore,
        HostServiceEndpointSnapshotPublisher Publisher);

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public List<Guid> StartedServices { get; } = [];

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            StartedServices.Add(specification.ServiceId);
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Accepted,
                ServiceStateReasonCode.StartAccepted,
                new ProcessInstanceId(Guid.CreateVersion7())));
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
    }

    private sealed class HealthyProbe : IServiceHealthProbe
    {
        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1));
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        public List<PortLeaseIntent> AcquireIntents { get; } = [];
        public int RenewCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            switch (intent.Kind)
            {
                case PortLeaseIntentKind.Acquire:
                    AcquireIntents.Add(intent);
                    var request = intent.Request!;
                    var now = DateTimeOffset.UtcNow;
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Applied,
                        new PortLease(
                            request.NodeId,
                            request.ServiceId,
                            request.Port == 0
                                ? request.AutomaticPortRangeStart!.Value
                                : request.Port,
                            now,
                            now.AddMinutes(1),
                            1)));
                case PortLeaseIntentKind.Renew:
                    RenewCalls++;
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.DatabaseUnavailable));
                case PortLeaseIntentKind.Release:
                    ReleaseCalls++;
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.NotFound));
                default:
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Rejected));
            }
        }
    }
}
