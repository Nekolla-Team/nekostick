using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceLifecycleManagerTests
{
    private static readonly Guid EagerServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000021");

    private static readonly Guid LazyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000022");

    private static readonly Guid DisabledServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000023");

    [Fact]
    public async Task ReconcileStartsOnlyEnabledEagerServices()
    {
        var eager = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var lazy = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var disabled = CreateService(DisabledServiceId, ServiceStartMode.Eager, enabled: false);
        var snapshot = CreateSnapshot(eager, lazy, disabled);
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, CancellationToken.None);

        Assert.Equal(new[] { EagerServiceId }, executor.StartedServices);
        Assert.True(publisher.Current.ContainsKey(EagerServiceId));
        Assert.False(publisher.Current.ContainsKey(LazyServiceId));
        Assert.False(publisher.Current.ContainsKey(DisabledServiceId));
    }

    [Fact]
    public async Task LazyServiceStartsOnlyOnEnsureReadyAndCoalescesConcurrentStarts()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor(blockStart: true);
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, new RecordingLeaseStore());

        var first = manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken).AsTask();
        await executor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken).AsTask();

        Assert.False(second.IsCompleted);
        executor.ReleaseStart();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(HostServiceReadinessStatus.Ready, result.Status));
        Assert.Equal(new[] { LazyServiceId }, executor.StartedServices);
        Assert.Single(publisher.Current);
    }

    [Fact]
    public async Task EndpointPublishesOnlyAfterHealthSucceeds()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var probe = new ControlledProbe();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, new RecordingExecutor(), probe, publisher, new RecordingLeaseStore());

        var readiness = manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken).AsTask();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(readiness.IsCompleted);
        Assert.Empty(publisher.Current);

        probe.Complete(HealthObservationStatus.Healthy);
        var result = await readiness;

        Assert.Equal(HostServiceReadinessStatus.Ready, result.Status);
        Assert.True(publisher.Current.ContainsKey(service.Id));
    }

    [Fact]
    public async Task FailedRenewalWithdrawsEndpointAndDisablesNewServices()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            AcquireLifetime = TimeSpan.FromSeconds(5)
        };
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtime = CreateRuntimeState(snapshot, holder);
        var manager = new HostServiceLifecycleManager(
            new RecordingExecutor(),
            new RecordingProbe(),
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false));

        var ready = await manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        Assert.True(publisher.Current.ContainsKey(service.Id));

        leaseStore.FailRenewal = true;
        await manager.RenewLeasesAsync(CancellationToken.None);

        Assert.Empty(publisher.Current);
        Assert.False(runtime.Status.DatabaseAvailable);
        Assert.False(runtime.NewServicesAllowed);
        var endpoint = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.False(endpoint.IsAvailable);
    }
    [Fact]
    public async Task StopAsyncQuiescesBlockedAutomaticStartupBeforeExecutorCleanup()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor(blockStart: true, ignoreStartCancellation: true);
        var leaseStore = new RecordingLeaseStore();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var startup = manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken).AsTask();
        await executor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stopping = manager.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);

        executor.ReleaseStart();
        await stopping;
        var readiness = await startup;

        Assert.Equal(HostServiceReadinessStatus.Cancelled, readiness.Status);
        Assert.Empty(executor.AcceptedServices);
        Assert.Empty(leaseStore.HeldLeases);
        Assert.Empty(publisher.Current);
        AssertReleaseIntent(leaseStore, new NodeIdentifier("node"), service.Id, 35000, 1);
    }

    [Fact]
    public async Task SameOwnerOutOfRangeAutomaticLeaseIsReleasedWithoutStarting()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLease = CreateReturnedLease(service.Id, 35100, version: 7)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.HeldLeases);
        AssertReleaseIntent(leaseStore, new NodeIdentifier("node"), service.Id, 35100, 7);
    }

    [Fact]
    public async Task SameOwnerExpiredAutomaticLeaseIsReleasedWithoutStarting()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLease = CreateReturnedLease(service.Id, 35001, version: 8, expired: true)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.HeldLeases);
        AssertReleaseIntent(leaseStore, new NodeIdentifier("node"), service.Id, 35001, 8);
    }


    [Fact]
    public async Task MismatchedNodeAutomaticLeaseIsNotReleased()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLease = CreateReturnedLease(
                service.Id,
                35003,
                version: 10,
                nodeId: new NodeIdentifier("other-node"))
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.ReleaseIntents);
        Assert.Single(leaseStore.HeldLeases);
    }

    [Fact]
    public async Task MismatchedServiceAutomaticLeaseIsNotReleased()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLease = CreateReturnedLease(service.Id, 35004, version: 11, serviceIdOverride: EagerServiceId)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.ReleaseIntents);
        Assert.Single(leaseStore.HeldLeases);
    }

    private static void AssertReleaseIntent(
        RecordingLeaseStore leaseStore,
        NodeIdentifier nodeId,
        Guid serviceId,
        int port,
        long version)
    {
        var intent = Assert.Single(leaseStore.ReleaseIntents);
        Assert.Equal(PortLeaseIntentKind.Release, intent.Kind);
        var release = Assert.IsType<PortLeaseRelease>(intent.Release);
        Assert.Equal(nodeId, release.NodeId);
        Assert.Equal(serviceId, release.ServiceId);
        Assert.Equal(port, release.Port);
        Assert.Equal(version, release.LeaseVersion);
    }

    private static PortLease CreateReturnedLease(
        Guid serviceId,
        int port,
        long version,
        NodeIdentifier? nodeId = null,
        bool expired = false,
        Guid? serviceIdOverride = null)
    {
        var owner = nodeId ?? new NodeIdentifier("node");
        if (expired)
        {
            return new PortLease(
                owner,
                serviceIdOverride ?? serviceId,
                port,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddTicks(1),
                version);
        }

        var acquiredAt = DateTimeOffset.UtcNow;
        return new PortLease(
            owner,
            serviceIdOverride ?? serviceId,
            port,
            acquiredAt,
            acquiredAt.AddMinutes(5),
            version);
    }


    private static HostServiceLifecycleManager CreateManager(
        HostConfigurationSnapshot snapshot,
        RecordingExecutor executor,
        IServiceHealthProbe probe,
        HostServiceEndpointSnapshotPublisher publisher,
        RecordingLeaseStore leaseStore)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtime = CreateRuntimeState(snapshot, holder);
        return new HostServiceLifecycleManager(
            executor,
            probe,
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false));
    }

    private static HostRuntimeState CreateRuntimeState(
        HostConfigurationSnapshot snapshot,
        HostConfigurationSnapshotHolder? holder = null)
    {
        holder ??= new HostConfigurationSnapshotHolder();
        if (holder.Current is null)
        {
            Assert.True(holder.TryReplace(snapshot));
        }

        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtime.MarkSnapshotAccepted();
        return runtime;
    }

    private static HostConfigurationSnapshot CreateSnapshot(params ServiceConfiguration[] services) =>
        new(
            1,
            new GlobalSettingsConfiguration(
                version: 1,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            services.ToImmutableArray(),
            default,
            default);

    private static ServiceConfiguration CreateService(
        Guid id,
        ServiceStartMode startMode,
        bool enabled) =>
        new(
            id,
            enabled,
            "/bin/service",
            ImmutableArray<string>.Empty,
            "/tmp",
            ImmutableDictionary<string, string>.Empty,
            startMode,
            Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);
    private static ServiceConfiguration CreateServiceWithEnvironment(
        Guid id,
        ImmutableDictionary<string, string> environment) =>
        new(
            id,
            true,
            "/bin/service",
            ImmutableArray<string>.Empty,
            "/tmp",
            environment,
            ServiceStartMode.Lazy,
            Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    private sealed class RecordingExecutor : IProcessExecutor
    {
        private readonly TaskCompletionSource<bool> _startGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockStart;
        private readonly bool _ignoreStartCancellation;

        public RecordingExecutor(bool blockStart = false, bool ignoreStartCancellation = false)
        {
            _blockStart = blockStart;
            _ignoreStartCancellation = ignoreStartCancellation;
        }

        public List<Guid> StartedServices { get; } = [];
        public List<Guid> AcceptedServices { get; } = [];
        public TaskCompletionSource<bool> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            StartedServices.Add(specification.ServiceId);
            StartEntered.TrySetResult(true);
            if (_blockStart)
            {
                if (_ignoreStartCancellation)
                {
                    await _startGate.Task.ConfigureAwait(false);
                }
                else
                {
                    await _startGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
            }

            AcceptedServices.Add(specification.ServiceId);
            return new(ProcessOperationStatus.Accepted, ServiceStateReasonCode.StartAccepted);
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));

        public void ReleaseStart() => _startGate.TrySetResult(true);
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        public TimeSpan AcquireLifetime { get; init; } = TimeSpan.FromMinutes(1);
        public bool FailRenewal { get; set; }
        public PortLease? ReturnedAcquireLease { get; set; }
        public List<PortLease> HeldLeases { get; } = [];
        public List<PortLeaseIntent> ReleaseIntents { get; } = [];

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind == PortLeaseIntentKind.Acquire)
            {
                var request = intent.Request!;
                var lease = ReturnedAcquireLease;
                if (lease is null)
                {
                    var port = request.Port == 0
                        ? request.AutomaticPortRangeStart!.Value
                        : request.Port;
                    var now = DateTimeOffset.UtcNow;
                    lease = new PortLease(
                        request.NodeId,
                        request.ServiceId,
                        port,
                        now,
                        now.Add(AcquireLifetime),
                        1);
                }

                HeldLeases.Add(lease);
                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.Applied,
                    lease));
            }

            if (intent.Kind == PortLeaseIntentKind.Release)
            {
                var release = intent.Release!;
                ReleaseIntents.Add(intent);
                HeldLeases.RemoveAll(lease =>
                    lease.NodeId == release.NodeId &&
                    lease.ServiceId == release.ServiceId &&
                    lease.Port == release.Port &&
                    lease.Version == release.LeaseVersion);
                return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
            }

            if (intent.Kind == PortLeaseIntentKind.Renew && FailRenewal)
            {
                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.DatabaseUnavailable));
            }

            return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
        }
    }


    private sealed class RecordingProbe : IServiceHealthProbe
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

    private sealed class ControlledProbe : IServiceHealthProbe
    {
        private readonly TaskCompletionSource<HealthObservationStatus> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            var status = await _result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HealthObservationResult(
                request.ServiceId,
                status,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1);
        }

        public void Complete(HealthObservationStatus status) => _result.TrySetResult(status);
    }


}
