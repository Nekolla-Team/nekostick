using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
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
    private static readonly Guid DependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000024");

    private static readonly Guid ConsumerServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000025");

    private static readonly Guid MissingDependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000026");

    private static readonly Guid DisabledDependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000027");

    private static readonly Guid CycleServiceAId =
        Guid.Parse("018f0000-0000-7000-8000-000000000028");

    private static readonly Guid CycleServiceBId =
        Guid.Parse("018f0000-0000-7000-8000-000000000029");
    private static readonly string[] ExpectedTemplateArguments =
        ["--dynamic", "35000", "--legacy", "35000"];

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
    public async Task OnDemandServiceIsNotAutomaticallyRetriedAfterDatabaseRecovery()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);
        runtime.MarkDatabaseUnavailable();

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, readiness.Status);
        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);

        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(PortLeaseOperationStatus.Rejected, false)]
    [InlineData(PortLeaseOperationStatus.Conflict, false)]
    [InlineData(PortLeaseOperationStatus.Applied, false)]
    public async Task EagerStartupDoesNotAutomaticallyRetryNonDatabaseLeaseFailures(
        PortLeaseOperationStatus status,
        bool validLease)
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((status, validLease));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.True(runtime.NewServicesAllowed);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EagerDependencyAndConsumerRecoverAfterDatabaseUnavailable()
    {
        var dependency = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            ImmutableArray.Create(string.Concat("${PORT@", EagerServiceId, "}")),
            ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.DatabaseUnavailable, true));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            consumer.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, initial.Status);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        Assert.True(manager.TryGet(consumer.Id, out var blockedConsumer));
        Assert.Equal(ExtensionServiceFailureCode.DependencyUnavailable, blockedConsumer.FailureCode);
        Assert.Equal("A required startup dependency is unavailable.", blockedConsumer.FailureReason);

        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, leaseStore.AcquireIntents.Count);
        Assert.Equal(new[] { dependency.Id, consumer.Id }, executor.StartedServices);
        Assert.True(manager.TryGet(dependency.Id, out var readyDependency));
        Assert.Equal(ExtensionServiceLifecycleState.Running, readyDependency.LifecycleState);
        Assert.True(manager.TryGet(consumer.Id, out var readyConsumer));
        Assert.Equal(ExtensionServiceLifecycleState.Running, readyConsumer.LifecycleState);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PermanentDependencyLeaseFailureDoesNotQueueEagerConsumerRetry()
    {
        var dependency = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            ImmutableArray.Create(string.Concat("${PORT@", EagerServiceId, "}")),
            ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.Conflict, true));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out _);

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            consumer.Id,
            TestContext.Current.CancellationToken);
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, initial.Status);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        Assert.True(manager.TryGet(consumer.Id, out var blockedConsumer));
        Assert.Equal(ExtensionServiceFailureCode.DependencyUnavailable, blockedConsumer.FailureCode);
        Assert.Equal("A required startup dependency is unavailable.", blockedConsumer.FailureReason);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EagerRetryDeadlineClearsAfterPermanentLeaseFailure()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.DatabaseUnavailable, true));
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.Conflict, true));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, initial.Status);
        Assert.True(manager.TryGet(service.Id, out var queuedRetry));
        Assert.NotNull(queuedRetry.RetryAt);

        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, leaseStore.AcquireIntents.Count);
        Assert.True(manager.TryGet(service.Id, out var terminalFailure));
        Assert.Equal(ExtensionServiceFailureCode.PortLeaseUnavailable, terminalFailure.FailureCode);
        Assert.Null(terminalFailure.RetryAt);

        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, leaseStore.AcquireIntents.Count);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigurationOnlyGateClosureDoesNotQueueEagerRetry()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);
        runtime.MarkSnapshotRejected();

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.True(manager.TryGet(service.Id, out var rejectedGate));
        Assert.Null(rejectedGate.RetryAt);
        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, initial.Status);
        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("disabled")]
    [InlineData("service-version")]
    [InlineData("global-version")]
    [InlineData("start-mode")]
    public async Task PendingEagerRetryIsDiscardedWhenAcceptedConfigurationChanges(string change)
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out var holder,
            out var runtime);
        runtime.MarkDatabaseUnavailable();

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);
        Assert.True(manager.TryGet(service.Id, out var queuedRetry));
        Assert.NotNull(queuedRetry.RetryAt);
        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);

        runtime.MarkSnapshotAccepted();
        var updated = change switch
        {
            "removed" => CreateSnapshot(2),
            "disabled" => CreateSnapshot(
                CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: false)),
            "service-version" => CreateSnapshot(
                2,
                CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true, version: 2)),
            "global-version" => CreateSnapshot(2, service),
            "start-mode" => CreateSnapshot(
                CreateService(EagerServiceId, ServiceStartMode.Lazy, enabled: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.True(holder.TryReplace(updated));

        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        if (change == "removed")
        {
            Assert.False(manager.TryGet(service.Id, out _));
        }
        else
        {
            Assert.True(manager.TryGet(service.Id, out var discardedRetry));
            Assert.Null(discardedRetry.RetryAt);
        }
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OwnedEagerRetryIsDiscardedWhenExtensionIsDisabled()
    {
        const string owner = "pending-eager-owner";
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var loaded = CreateOwnedSnapshot(
            1,
            service,
            owner,
            Nekolla.Nekostick.Contracts.ExtensionLoadState.Loaded);
        var disabled = CreateOwnedSnapshot(
            1,
            service,
            owner,
            Nekolla.Nekostick.Contracts.ExtensionLoadState.Disabled);
        var serviceOwners = ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            loaded,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out var holder,
            out var runtime,
            serviceOwners);
        runtime.MarkDatabaseUnavailable();

        await manager.ReconcileAsync(loaded, TestContext.Current.CancellationToken);
        Assert.True(manager.TryGet(service.Id, out var queuedRetry));
        Assert.NotNull(queuedRetry.RetryAt);
        Assert.Empty(leaseStore.AcquireIntents);

        runtime.MarkSnapshotAccepted();
        Assert.Equal(
            SnapshotAdmission.Accepted,
            holder.TryReplace(disabled, dispatchGeneration: null, serviceOwners));
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        Assert.True(manager.TryGet(service.Id, out var disabledRuntime));
        Assert.Null(disabledRuntime.RetryAt);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualReadinessJoinsEagerRetryWithoutStartingDuplicateProcess()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor(blockStart: true);
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);
        runtime.MarkDatabaseUnavailable();
        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        runtime.MarkSnapshotAccepted();
        var retry = manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);
        await executor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var manual = manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken).AsTask();
        Assert.False(manual.IsCompleted);

        executor.ReleaseStart();
        await retry.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var readiness = await manual;

        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Equal(new[] { EagerServiceId }, executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LaunchTemplatesResolveDynamicLegacyAndOwnEnvironmentValues()
    {
        var service = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ["--dynamic", "${PORT}", "--legacy", "$PORT"],
            environment: ImmutableDictionary<string, string>.Empty.Add("SELF", "self-value"));
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        var specification = Assert.Single(executor.StartedSpecifications);
        Assert.Equal(ExpectedTemplateArguments, specification.Arguments);
        Assert.Equal("self-value", specification.Environment.Values["SELF"]);
    }

    [Fact]
    public async Task RemoteTemplateResolvesPublishedDependencyEnvironment()
    {
        var dependency = CreateService(DependencyServiceId, ServiceStartMode.Lazy, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add(
                "REMOTE_PORT",
                string.Concat("${PORT@", DependencyServiceId, "}")));
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshot, dependency.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshot, consumer.Id, TestContext.Current.CancellationToken)).Status);

        var specification = Assert.Single(
            executor.StartedSpecifications,
            value => value.ServiceId == consumer.Id);
        Assert.Equal("35000", specification.Environment.Values["REMOTE_PORT"]);
    }

    [Fact]
    public async Task ReconcileStartsEagerDependenciesBeforeConsumers()
    {
        var dependency = CreateService(DependencyServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", DependencyServiceId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { DependencyServiceId, ConsumerServiceId }, executor.StartedServices);
        var consumerSpecification = Assert.Single(
            executor.StartedSpecifications,
            value => value.ServiceId == ConsumerServiceId);
        Assert.Equal("35000", consumerSpecification.Arguments[0]);
    }

    [Fact]
    public async Task UnsatisfiedMissingOrDisabledDependencyLeavesOtherEagerServicesRunning()
    {
        var healthy = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var missing = CreateServiceWithLaunch(
            MissingDependencyServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", Guid.Parse("018f0000-0000-7000-8000-000000000030"), "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var disabledDependency = CreateService(DisabledDependencyServiceId, ServiceStartMode.Eager, enabled: false);
        var disabledConsumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", DisabledDependencyServiceId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(healthy, missing, disabledDependency, disabledConsumer);
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            publisher,
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Contains(EagerServiceId, executor.StartedServices);
        Assert.DoesNotContain(MissingDependencyServiceId, executor.StartedServices);
        Assert.DoesNotContain(ConsumerServiceId, executor.StartedServices);
        Assert.True(publisher.Current.ContainsKey(EagerServiceId));
    }

    [Fact]
    public async Task EagerDependencyCycleFailsWithoutHanging()
    {
        var serviceA = CreateServiceWithLaunch(
            CycleServiceAId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", CycleServiceBId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var serviceB = CreateServiceWithLaunch(
            CycleServiceBId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", CycleServiceAId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(serviceA, serviceB);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(executor.StartedServices);
    }

    [Fact]
    public async Task ReadCurrentReturnsSnapshotsForAnyActiveServiceCount()
    {
        var eager = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(eager);
        var manager = CreateManager(
            snapshot,
            new RecordingExecutor(),
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, CancellationToken.None);

        // A single active service leaves Count != Capacity in the builder;
        // MoveToImmutable would throw here.
        var current = manager.ReadCurrent();
        var entry = Assert.Single(current);
        Assert.Equal(EagerServiceId, entry.ServiceId);
    }
    [Fact]
    public async Task DisabledOwnerServicesStayOutOfReconcileUntilTheOwnerIsEnabled()
    {
        const string owner = "disabled.owner";
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var loadedSnapshot = CreateOwnedSnapshot(
            1,
            service,
            owner,
            Contracts.ExtensionLoadState.Loaded);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(
            loadedSnapshot,
            dispatchGeneration: null,
            ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner)));
        var runtime = CreateRuntimeState(loadedSnapshot, holder);
        var executor = new RecordingExecutor();
        var endpointPublisher = new HostServiceEndpointSnapshotPublisher();
        var manager = new HostServiceLifecycleManager(
            executor,
            new RecordingProbe(),
            new RecordingLeaseStore(),
            holder,
            endpointPublisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));

        await manager.ReconcileAsync(loadedSnapshot, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { service.Id }, executor.StartedServices);
        Assert.True(endpointPublisher.Current.ContainsKey(service.Id));

        var disabledSnapshot = CreateOwnedSnapshot(
            2,
            service,
            owner,
            Contracts.ExtensionLoadState.Disabled);
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(
            disabledSnapshot,
            dispatchGeneration: null,
            ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner)));
        await manager.ReconcileAsync(disabledSnapshot, TestContext.Current.CancellationToken);
        Assert.Contains(service.Id, executor.StoppedServices);
        Assert.False(endpointPublisher.Current.ContainsKey(service.Id));
        var disabledReadiness = await manager.EnsureReadyAsync(
            disabledSnapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Disabled, disabledReadiness.Status);

        var enabledSnapshot = CreateOwnedSnapshot(
            3,
            service,
            owner,
            Contracts.ExtensionLoadState.Loaded);
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(
            enabledSnapshot,
            dispatchGeneration: null,
            ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner)));
        await manager.ReconcileAsync(enabledSnapshot, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { service.Id, service.Id }, executor.StartedServices);
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
    public async Task PublishVerifiedEndpointsFiltersByReadyServiceAndPort()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(snapshot, new RecordingExecutor(), new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);

        var readyLease = Assert.Single(leaseStore.HeldLeases);
        publisher.Publish(Array.Empty<HostServiceEndpointLease>());
        await manager.PublishVerifiedEndpointsAsync(
        [
            new HostServiceEndpointLease(service.Id, readyLease.Port + 1, readyLease.ExpiresAt)
        ]);
        Assert.Empty(publisher.Current);

        var matchingLease = new HostServiceEndpointLease(
            service.Id,
            readyLease.Port,
            readyLease.ExpiresAt);
        await manager.PublishVerifiedEndpointsAsync([matchingLease]);
        Assert.Equal(matchingLease, publisher.Current[service.Id]);

        await manager.StopAsync(CancellationToken.None);
        await manager.PublishVerifiedEndpointsAsync([matchingLease]);
        Assert.Empty(publisher.Current);
    }

    [Fact]
    public async Task ServiceAndEndpointTransitionsReachServingExtensionCoreEventQueue()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifestResult = ExtensionManifestDiscovery.Discover(fixture.RootPath);
        Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
        var manifest = manifestResult.Manifest!;
        await using var extensions = new ExtensionRuntimeManager(HostApiVersion.Current);
        var settings = new ExtensionSettingsConfiguration(
            manifest.Id,
            1,
            JsonSerializer.Serialize(new { publishCoreEvents = true, eventCount = 5 }),
            1);
        Assert.True((await extensions.LoadAsync(
            manifest,
            settings,
            TestContext.Current.CancellationToken)).Succeeded);

        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtimeState = CreateRuntimeState(snapshot, holder);
        var endpointPublisher = new HostServiceEndpointSnapshotPublisher(extensions);
        var manager = new HostServiceLifecycleManager(
            new RecordingExecutor(),
            new RecordingProbe(),
            new RecordingLeaseStore(),
            holder,
            endpointPublisher,
            runtimeState,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false),
            extensions);

        var ready = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        await manager.StopAsync(CancellationToken.None);

        var result = await extensions.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/lifecycle-events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        var body = Encoding.UTF8.GetString(result.Response!.Body.AsSpan());
        Assert.Contains("\"state\":\"Loaded\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"serviceId\":\"{service.Id}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"ready\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"stopped\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"withdrawn\"", body, StringComparison.Ordinal);
    }


    [Fact]
    public async Task FailedRenewalKeepsEndpointAndDisablesNewServices()
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
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));

        var ready = await manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        Assert.True(publisher.Current.ContainsKey(service.Id));

        leaseStore.FailRenewal = true;
        await manager.RenewLeasesAsync(CancellationToken.None);

        Assert.True(publisher.Current.ContainsKey(service.Id));
        Assert.False(runtime.Status.DatabaseAvailable);
        Assert.False(runtime.NewServicesAllowed);
        var endpoint = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.True(endpoint.IsAvailable);
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
        RecordingLeaseStore leaseStore) =>
        CreateManager(snapshot, executor, probe, publisher, leaseStore, out _, out _);

    private static HostServiceLifecycleManager CreateManager(
        HostConfigurationSnapshot snapshot,
        RecordingExecutor executor,
        IServiceHealthProbe probe,
        HostServiceEndpointSnapshotPublisher publisher,
        RecordingLeaseStore leaseStore,
        out HostConfigurationSnapshotHolder holder,
        out HostRuntimeState runtime,
        ImmutableDictionary<Guid, string?>? serviceOwners = null)
    {
        holder = new HostConfigurationSnapshotHolder();
        if (serviceOwners is null)
        {
            Assert.True(holder.TryReplace(snapshot));
        }
        else
        {
            Assert.Equal(
                SnapshotAdmission.Accepted,
                holder.TryReplace(snapshot, dispatchGeneration: null, serviceOwners));
        }

        runtime = CreateRuntimeState(snapshot, holder);
        return new HostServiceLifecycleManager(
            executor,
            probe,
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));
    }
    private static HostConfigurationSnapshot CreateSnapshot(params ServiceConfiguration[] services) =>
        CreateSnapshot(1, services);

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        params ServiceConfiguration[] services) =>
        new(
            version,
            new GlobalSettingsConfiguration(
                version: version,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            services.ToImmutableArray(),
            default,
            default);

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
    private static HostConfigurationSnapshot CreateOwnedSnapshot(
        long version,
        ServiceConfiguration service,
        string owner,
        Contracts.ExtensionLoadState state) =>
        new(
            version,
            new GlobalSettingsConfiguration(
                version: version,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            ImmutableArray.Create(service),
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                owner,
                "1.0.0",
                state,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1)),
            default);


    private static ServiceConfiguration CreateService(
        Guid id,
        ServiceStartMode startMode,
        bool enabled,
        long version = 1) =>
        new(
            id,
            enabled,
            "/bin/sh",
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
            version);
    private static ServiceConfiguration CreateServiceWithLaunch(
        Guid id,
        ServiceStartMode startMode,
        bool enabled,
        ImmutableArray<string> arguments,
        ImmutableDictionary<string, string> environment) =>
        new(
            id,
            enabled,
            "/bin/sh",
            arguments,
            "/tmp",
            environment,
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
            "/bin/sh",
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
        public List<ProcessLaunchSpecification> StartedSpecifications { get; } = [];
        public List<Guid> StoppedServices { get; } = [];
        public List<Guid> AcceptedServices { get; } = [];
        public TaskCompletionSource<bool> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            StartedServices.Add(specification.ServiceId);
            StartedSpecifications.Add(specification);
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
            CancellationToken cancellationToken = default)
        {
            StoppedServices.Add(serviceId);
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }

        public void ReleaseStart() => _startGate.TrySetResult(true);
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        public TimeSpan AcquireLifetime { get; init; } = TimeSpan.FromMinutes(1);
        public bool FailRenewal { get; set; }
        public PortLease? ReturnedAcquireLease { get; set; }
        public List<PortLease> HeldLeases { get; } = [];
        public List<PortLeaseIntent> ReleaseIntents { get; } = [];
        public Queue<(PortLeaseOperationStatus Status, bool ValidLease)> AcquireResults { get; } = new();
        public List<PortLeaseIntent> AcquireIntents { get; } = [];

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind == PortLeaseIntentKind.Acquire)
            {
                AcquireIntents.Add(intent);
                var outcome = AcquireResults.Count == 0
                    ? (Status: PortLeaseOperationStatus.Applied, ValidLease: true)
                    : AcquireResults.Dequeue();
                if (outcome.Status != PortLeaseOperationStatus.Applied)
                {
                    return ValueTask.FromResult(new PortLeaseOperationResult(outcome.Status));
                }

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

                if (!outcome.ValidLease)
                {
                    lease = new PortLease(
                        lease.NodeId,
                        LazyServiceId,
                        lease.Port,
                        lease.AcquiredAt,
                        lease.ExpiresAt,
                        lease.Version);
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
