using System.Collections.Immutable;
using System.Net.Http;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Supervision;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceRuntimeObservabilityTests
{
    private static readonly Guid HealthFailureServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000075");
    private static readonly Guid SpawnFailureServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000076");
    private static readonly Guid ProcessExitServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000077");
    private static readonly Guid RegistryFirstServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000078");
    private static readonly Guid RegistrySecondServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000079");
    private static readonly Guid RuntimeProbeBoundaryServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000080");
    private static readonly Guid LeaseRenewalServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000081");
    private static readonly Guid CleanExitServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000082");
    private static readonly Guid RestartBailoutServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000084");

    private static readonly Guid EagerRetryServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000085");
    private static readonly Guid ShutdownRetryServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000086");

    private static readonly Guid CandidateProgressServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000083");
    private static readonly bool[] ExpectedInitialFlags = [true, true, false, false];

    [Fact]
    public async Task FailedStartupRetainsProbeDetailsUntilTheNextLifecycleTransition()
    {
        const string failureDetail = "connection refused";
        var service = CreateService(HealthFailureServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Http);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var executor = new RecordingExecutor();
        using var client = new HttpClient(new FailingHttpHandler(new HttpRequestException(failureDetail)));
        using var probe = new ServiceHealthProbe(executor, client);
        var manager = CreateManager(holder, executor, probe);

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);

            Assert.True(manager.TryGet(service.Id, out var failed));
            Assert.Equal(ExtensionServiceLifecycleState.Failed, failed.LifecycleState);
            Assert.Equal(ExtensionServiceFailureStage.HealthProbe, failed.FailureStage);
            Assert.Equal(ExtensionServiceFailureCode.RuntimeUnavailable, failed.FailureCode);
            Assert.Equal("http://127.0.0.1:35000", failed.LastProbe?.Target);
            Assert.Equal(failureDetail, failed.LastProbe?.ErrorMessage);

            Assert.True(manager.TryGet(service.Id, out var retained));
            Assert.Same(failed, retained);

            var disabledConfiguration = CreateSnapshot(
                2,
                CreateService(HealthFailureServiceId, version: 2, enabled: false, healthCheckType: ServiceHealthCheckType.Http));
            Assert.True(holder.TryReplace(disabledConfiguration));
            await manager.ReconcileAsync(disabledConfiguration, TestContext.Current.CancellationToken);

            Assert.True(manager.TryGet(service.Id, out var disabled));
            Assert.NotSame(failed, disabled);
            Assert.Equal(ExtensionServiceLifecycleState.Disabled, disabled.LifecycleState);
            Assert.Equal(ExtensionServiceFailureStage.None, disabled.FailureStage);
            Assert.Null(disabled.LastProbe);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RuntimeProbeDetailIsBoundedBeforeContractsSnapshotPublication()
    {
        var service = CreateService(RuntimeProbeBoundaryServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Http);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var errorMessage = new string('x', 1024);
        var manager = CreateManager(holder, new RecordingExecutor(), new LongDiagnosticProbe(errorMessage));

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);

            Assert.True(manager.TryGet(service.Id, out var failed));
            Assert.Equal(ExtensionServiceFailureStage.HealthProbe, failed.FailureStage);
            Assert.Equal(errorMessage[..512], failed.LastProbe?.ErrorMessage);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SpawnRejectionIsClassifiedAtSpawnStage()
    {
        var service = CreateService(SpawnFailureServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Process);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var manager = CreateManager(
            holder,
            new RecordingExecutor(rejectStart: true),
            new HealthyProbe());

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);

            Assert.True(manager.TryGet(service.Id, out var failed));
            Assert.Equal(ExtensionServiceLifecycleState.Failed, failed.LifecycleState);
            Assert.Equal(ExtensionServiceFailureStage.Spawn, failed.FailureStage);
            Assert.Equal(ExtensionServiceFailureCode.StartRejected, failed.FailureCode);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LeaseRenewalOwnershipLossIsPublishedBeforeProcessStop()
    {
        var service = CreateService(LeaseRenewalServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Process);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var leaseStore = new LeaseOwnershipConflictStore();
        HostServiceLifecycleManager? manager = null;
        var failureAtStop = new TaskCompletionSource<HostServiceRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RecordingExecutor(onStop: () =>
        {
            if (manager is { } lifecycleManager &&
                lifecycleManager.TryGet(service.Id, out var snapshot) &&
                snapshot.FailureCode == ExtensionServiceFailureCode.PortLeaseUnavailable)
            {
                failureAtStop.TrySetResult(snapshot);
            }
        });
        var lifecycleManager = CreateManager(holder, executor, new HealthyProbe(), leaseStore);
        manager = lifecycleManager;

        try
        {
            await lifecycleManager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);
            await lifecycleManager.RenewLeasesAsync(TestContext.Current.CancellationToken);

            var failureAtStopSnapshot = await failureAtStop.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionServiceLifecycleState.Failed, failureAtStopSnapshot.LifecycleState);
            Assert.Equal(ExtensionServiceFailureStage.Spawn, failureAtStopSnapshot.FailureStage);
            Assert.Equal(ExtensionServiceFailureCode.PortLeaseUnavailable, failureAtStopSnapshot.FailureCode);
            Assert.NotNull(failureAtStopSnapshot.ProcessInstanceId);
            Assert.Equal(1, leaseStore.RenewalCount);

            var stopped = await WaitForRuntimeSnapshotAsync(
                lifecycleManager,
                service.Id,
                snapshot => snapshot.LifecycleState == ExtensionServiceLifecycleState.Failed &&
                    snapshot.FailureCode == ExtensionServiceFailureCode.PortLeaseUnavailable &&
                    snapshot.ProcessInstanceId is null,
                TestContext.Current.CancellationToken);
            Assert.Null(stopped.ProcessId);
        }
        finally
        {
            await lifecycleManager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task StartupProcessExitPublishesExitCodeAndProcessExitStage()
    {
        var service = CreateService(ProcessExitServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Http);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var executor = new RecordingExecutor();
        var probe = new GatedProbe();
        var manager = CreateManager(holder, executor, probe);

        try
        {
            var startup = manager.EnsureReadyAsync(
                configuration,
                service.Id,
                TestContext.Current.CancellationToken).AsTask();
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var instanceId = executor.InstanceId ?? throw new InvalidOperationException("The process instance was not started.");
            const int exitCode = 23;

            await manager.NotifyProcessExitAsync(
                service.Id,
                instanceId,
                successfulExit: false,
                exitCode: exitCode);
            var observedExit = await WaitForRuntimeSnapshotAsync(
                manager,
                service.Id,
                snapshot => snapshot.ProcessExitCode == exitCode,
                TestContext.Current.CancellationToken);

            Assert.Equal(ExtensionServiceFailureStage.ProcessExit, observedExit.FailureStage);
            Assert.Equal(exitCode, observedExit.ProcessExitCode);

            probe.Release();
            await startup.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(manager.TryGet(service.Id, out var failed));
            Assert.Equal(ExtensionServiceLifecycleState.Failed, failed.LifecycleState);
            Assert.Equal(ExtensionServiceFailureStage.ProcessExit, failed.FailureStage);
            Assert.Equal(ExtensionServiceFailureCode.ProcessExited, failed.FailureCode);
            Assert.Equal(exitCode, failed.ProcessExitCode);
        }
        finally
        {
            probe.Release();
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SuccessfulCleanExitPublishesStoppedWithoutFailure()
    {
        var service = CreateService(CleanExitServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Process);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var executor = new RecordingExecutor();
        var manager = CreateManager(holder, executor, new HealthyProbe());

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);
            var instanceId = executor.InstanceId ?? throw new InvalidOperationException("The process instance was not started.");

            await manager.NotifyProcessExitAsync(
                service.Id,
                instanceId,
                successfulExit: true,
                exitCode: 0);
            var stopped = await WaitForRuntimeSnapshotAsync(
                manager,
                service.Id,
                snapshot => snapshot.LifecycleState == ExtensionServiceLifecycleState.Stopped &&
                    snapshot.FailureCode == ExtensionServiceFailureCode.None,
                TestContext.Current.CancellationToken);

            Assert.Equal(ExtensionServiceFailureStage.None, stopped.FailureStage);
            Assert.Equal(0, stopped.ProcessExitCode);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }
    [Fact]
    public async Task RestartBailoutPublishesStoppedStateForUpdatedServiceConfiguration()
    {
        var service = CreateService(
            RestartBailoutServiceId,
            version: 1,
            enabled: true,
            healthCheckType: ServiceHealthCheckType.Process,
            restartPolicy: Contracts.ServiceRestartPolicy.OnFailure);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var executor = new RecordingExecutor();
        var manager = CreateManager(holder, executor, new HealthyProbe());

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);
            var instanceId = executor.InstanceId ?? throw new InvalidOperationException("The process instance was not started.");
            var updatedService = CreateService(
                RestartBailoutServiceId,
                version: 2,
                enabled: true,
                healthCheckType: ServiceHealthCheckType.Process,
                restartPolicy: Contracts.ServiceRestartPolicy.OnFailure);
            var updatedConfiguration = CreateSnapshot(2, updatedService);
            Assert.True(holder.TryReplace(updatedConfiguration));

            await manager.NotifyProcessExitAsync(
                service.Id,
                instanceId,
                successfulExit: false,
                exitCode: 23);

            var stopped = await WaitForRuntimeSnapshotAsync(
                manager,
                service.Id,
                snapshot => snapshot.LifecycleState == ExtensionServiceLifecycleState.Stopped &&
                    snapshot.ConfigurationVersion == updatedConfiguration.Version,
                TestContext.Current.CancellationToken);
            Assert.Null(stopped.ProcessId);
            Assert.Equal(ExtensionServiceFailureCode.None, stopped.FailureCode);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RestartBailoutPublishesRuntimeUnavailableWhenStartsAreDisallowed()
    {
        var service = CreateService(
            RestartBailoutServiceId,
            version: 1,
            enabled: true,
            healthCheckType: ServiceHealthCheckType.Process,
            restartPolicy: Contracts.ServiceRestartPolicy.OnFailure);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtime.MarkSnapshotAccepted();
        var executor = new RecordingExecutor();
        var manager = CreateManager(holder, executor, new HealthyProbe(), runtimeState: runtime);

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);
            var instanceId = executor.InstanceId ?? throw new InvalidOperationException("The process instance was not started.");
            runtime.MarkDatabaseUnavailable();

            await manager.NotifyProcessExitAsync(
                service.Id,
                instanceId,
                successfulExit: false,
                exitCode: 23);

            var unavailable = await WaitForRuntimeSnapshotAsync(
                manager,
                service.Id,
                snapshot => snapshot.LifecycleState == ExtensionServiceLifecycleState.Failed &&
                    snapshot.FailureStage == ExtensionServiceFailureStage.Spawn &&
                    snapshot.FailureCode == ExtensionServiceFailureCode.RuntimeUnavailable,
                TestContext.Current.CancellationToken);
            Assert.Null(unavailable.ProcessId);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }


    [Fact]
    public async Task CandidateProbeSurvivesOldGenerationHealthUpdate()
    {
        var service = CreateService(CandidateProgressServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Process);
        var configuration = CreateSnapshot(1, service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var executor = new RecordingExecutor();
        var probe = new CandidateProgressProbe();
        var manager = CreateManager(holder, executor, probe);

        try
        {
            await manager.EnsureReadyAsync(configuration, service.Id, TestContext.Current.CancellationToken);
            var oldInstanceId = executor.InstanceId ?? throw new InvalidOperationException("The process instance was not started.");
            var replacementService = CreateService(
                CandidateProgressServiceId,
                version: 2,
                enabled: true,
                healthCheckType: ServiceHealthCheckType.Process);
            var replacementConfiguration = CreateSnapshot(2, replacementService);
            Assert.True(holder.TryReplace(replacementConfiguration));
            var replacementStartup = manager.EnsureReadyAsync(
                replacementConfiguration,
                service.Id,
                TestContext.Current.CancellationToken).AsTask();

            await probe.CandidateRetryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var candidateObservation = await WaitForRuntimeSnapshotAsync(
                manager,
                service.Id,
                snapshot => snapshot.LifecycleState == ExtensionServiceLifecycleState.Running &&
                    snapshot.LastProbe?.ErrorMessage == CandidateProgressProbe.CandidateFailureDetail,
                TestContext.Current.CancellationToken);
            Assert.Equal(oldInstanceId, candidateObservation.ProcessInstanceId);
            Assert.False(replacementStartup.IsCompleted);

            await ObserveReadyHealthAsync(manager, TestContext.Current.CancellationToken);
            Assert.True(manager.TryGet(service.Id, out var refreshed));
            Assert.Equal(ExtensionServiceLifecycleState.Running, refreshed.LifecycleState);
            Assert.Equal(oldInstanceId, refreshed.ProcessInstanceId);
            Assert.Equal(CandidateProgressProbe.CandidateFailureDetail, refreshed.LastProbe?.ErrorMessage);
        }
        finally
        {
            probe.ReleaseCandidateRetry();
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task EagerRetryDeadlinesArePublishedRescheduledAndClearedForWatchers()
    {
        var retryService = CreateService(
            EagerRetryServiceId,
            version: 1,
            enabled: true,
            healthCheckType: ServiceHealthCheckType.Process,
            startMode: ServiceStartMode.Eager);
        var shutdownService = CreateService(
            ShutdownRetryServiceId,
            version: 1,
            enabled: true,
            healthCheckType: ServiceHealthCheckType.Process,
            startMode: ServiceStartMode.Eager);
        var configuration = CreateSnapshot(1, retryService, shutdownService);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(configuration));
        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtime.MarkSnapshotAccepted();
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue(PortLeaseOperationStatus.DatabaseUnavailable);
        leaseStore.AcquireResults.Enqueue(PortLeaseOperationStatus.DatabaseUnavailable);
        await using var registry = new HostServiceRuntimeRegistry();
        var manager = CreateManager(
            holder,
            executor,
            new HealthyProbe(),
            leaseStore,
            runtime,
            registry);
        var changes = Channel.CreateUnbounded<HostServiceRuntimeStateChange>();
        using var subscription = registry.Subscribe(change => changes.Writer.TryWrite(change));
        var stopped = false;

        try
        {
            var initial = await manager.EnsureReadyAsync(
                configuration,
                retryService.Id,
                TestContext.Current.CancellationToken);
            Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, initial.Status);
            Assert.True(manager.TryGet(retryService.Id, out var queued));
            var firstRetryAt = queued.RetryAt ?? throw new InvalidOperationException("The eager retry deadline was not published.");
            var queuedUpdate = await WaitForRuntimeSnapshotChangeAsync(
                changes.Reader,
                retryService.Id,
                snapshot => snapshot.RetryAt == firstRetryAt,
                TestContext.Current.CancellationToken);
            Assert.Equal(firstRetryAt, queuedUpdate.RetryAt);

            runtime.MarkSnapshotAccepted();
            await manager.RetryWaitingServicesAsync(
                DateTimeOffset.MaxValue,
                TestContext.Current.CancellationToken);
            Assert.True(manager.TryGet(retryService.Id, out var rescheduled));
            var secondRetryAt = rescheduled.RetryAt ?? throw new InvalidOperationException("The eager retry deadline was not rescheduled.");
            Assert.True(secondRetryAt > firstRetryAt);
            var rescheduledUpdate = await WaitForRuntimeSnapshotChangeAsync(
                changes.Reader,
                retryService.Id,
                snapshot => snapshot.RetryAt == secondRetryAt,
                TestContext.Current.CancellationToken);
            Assert.Equal(secondRetryAt, rescheduledUpdate.RetryAt);

            runtime.MarkSnapshotAccepted();
            await manager.RetryWaitingServicesAsync(
                DateTimeOffset.MaxValue,
                TestContext.Current.CancellationToken);
            var startedUpdate = await WaitForRuntimeSnapshotChangeAsync(
                changes.Reader,
                retryService.Id,
                snapshot => snapshot.LifecycleState == ExtensionServiceLifecycleState.Running &&
                    snapshot.RetryAt is null,
                TestContext.Current.CancellationToken);
            Assert.Null(startedUpdate.RetryAt);

            runtime.MarkDatabaseUnavailable();
            var shutdownRetry = await manager.EnsureReadyAsync(
                configuration,
                shutdownService.Id,
                TestContext.Current.CancellationToken);
            Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, shutdownRetry.Status);
            Assert.True(manager.TryGet(shutdownService.Id, out var shutdownQueued));
            var shutdownRetryAt = shutdownQueued.RetryAt ?? throw new InvalidOperationException("The shutdown retry deadline was not published.");
            var shutdownQueuedUpdate = await WaitForRuntimeSnapshotChangeAsync(
                changes.Reader,
                shutdownService.Id,
                snapshot => snapshot.RetryAt == shutdownRetryAt,
                TestContext.Current.CancellationToken);
            Assert.Equal(shutdownRetryAt, shutdownQueuedUpdate.RetryAt);

            await manager.StopAsync(CancellationToken.None);
            stopped = true;
            Assert.True(registry.TryGet(shutdownService.Id, out var shutdownCleared));
            Assert.Null(shutdownCleared.RetryAt);
            var shutdownClearedUpdate = await WaitForRuntimeSnapshotChangeAsync(
                changes.Reader,
                shutdownService.Id,
                snapshot => snapshot.RetryAt is null,
                TestContext.Current.CancellationToken);
            Assert.Null(shutdownClearedUpdate.RetryAt);
        }
        finally
        {
            if (!stopped)
            {
                await manager.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task RegistryReplaysInitialSnapshotsThenEmitsOrderedUpdatesAndRemoval()
    {
        await using var registry = new HostServiceRuntimeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistryFirstServiceId,
                ExtensionServiceLifecycleState.Starting,
                now,
                ownerExtensionId: "owner-first"),
            serviceVersion: 1,
            enabled: true);
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistrySecondServiceId,
                ExtensionServiceLifecycleState.Failed,
                now.AddSeconds(1),
                ownerExtensionId: "owner-second"),
            serviceVersion: 1,
            enabled: true);

        var changes = Channel.CreateUnbounded<HostServiceRuntimeStateChange>();
        using var subscription = registry.Subscribe(change => changes.Writer.TryWrite(change));
        var delivered = new List<HostServiceRuntimeStateChange>
        {
            await changes.Reader.ReadAsync(TestContext.Current.CancellationToken),
            await changes.Reader.ReadAsync(TestContext.Current.CancellationToken)
        };

        registry.Publish(
            CreateRuntimeSnapshot(
                RegistryFirstServiceId,
                ExtensionServiceLifecycleState.Running,
                now.AddSeconds(2),
                ownerExtensionId: "owner-first"),
            serviceVersion: 1,
            enabled: true);
        registry.SynchronizeConfiguration(
            configuration: CreateSnapshot(
                1,
                CreateService(RegistryFirstServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Process)),
            enabledServices: new HashSet<Guid> { RegistryFirstServiceId },
            lifecycleWork: new HashSet<Guid> { RegistryFirstServiceId },
            serviceOwners: null,
            now: now.AddSeconds(3));
        delivered.Add(await changes.Reader.ReadAsync(TestContext.Current.CancellationToken));
        delivered.Add(await changes.Reader.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 1, 2, 3, 4 }, delivered.Select(change => change.Sequence));
        Assert.Equal(
            new[]
            {
                ExtensionServiceRuntimeStateChangeKind.Snapshot,
                ExtensionServiceRuntimeStateChangeKind.Snapshot,
                ExtensionServiceRuntimeStateChangeKind.Snapshot,
                ExtensionServiceRuntimeStateChangeKind.Removed
            },
            delivered.Select(change => change.Kind));
        Assert.Equal(ExpectedInitialFlags, delivered.Select(change => change.IsInitialSnapshot));
        Assert.Equal(RegistryFirstServiceId, delivered[0].ServiceId);
        Assert.Equal(RegistrySecondServiceId, delivered[1].ServiceId);
        Assert.Equal(ExtensionServiceLifecycleState.Running, delivered[2].Snapshot?.LifecycleState);
        Assert.Equal("owner-first", delivered[2].OwnerExtensionId);
        Assert.Null(delivered[3].Snapshot);
        Assert.Equal("owner-second", delivered[3].OwnerExtensionId);
    }

    [Fact]
    public async Task RuntimeSubscriptionCoalescesPendingSnapshotsAndPreservesRemoval()
    {
        await using var registry = new HostServiceRuntimeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistryFirstServiceId,
                ExtensionServiceLifecycleState.Starting,
                now,
                ownerExtensionId: "owner-first"),
            serviceVersion: 1,
            enabled: true);
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistrySecondServiceId,
                ExtensionServiceLifecycleState.Running,
                now.AddSeconds(1),
                ownerExtensionId: "owner-second"),
            serviceVersion: 1,
            enabled: true);

        var changes = Channel.CreateUnbounded<HostServiceRuntimeStateChange>();
        var firstSnapshotEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSnapshot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = (IAsyncDisposable)registry.Subscribe(change =>
        {
            changes.Writer.TryWrite(change);
            if (change.ServiceId == RegistryFirstServiceId && change.IsInitialSnapshot)
            {
                firstSnapshotEntered.TrySetResult(true);
                releaseFirstSnapshot.Task.GetAwaiter().GetResult();
            }
        });

        try
        {
            await firstSnapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            registry.Publish(
                CreateRuntimeSnapshot(
                    RegistryFirstServiceId,
                    ExtensionServiceLifecycleState.Running,
                    now.AddSeconds(2),
                    ownerExtensionId: "owner-first"),
                serviceVersion: 1,
                enabled: true);
            registry.Publish(
                CreateRuntimeSnapshot(
                    RegistryFirstServiceId,
                    ExtensionServiceLifecycleState.Failed,
                    now.AddSeconds(3),
                    ownerExtensionId: "owner-first"),
                serviceVersion: 1,
                enabled: true);
            registry.Publish(
                CreateRuntimeSnapshot(
                    RegistrySecondServiceId,
                    ExtensionServiceLifecycleState.Starting,
                    now.AddSeconds(4),
                    ownerExtensionId: "owner-second"),
                serviceVersion: 1,
                enabled: true);
            registry.Publish(
                CreateRuntimeSnapshot(
                    RegistrySecondServiceId,
                    ExtensionServiceLifecycleState.Failed,
                    now.AddSeconds(5),
                    ownerExtensionId: "owner-second"),
                serviceVersion: 1,
                enabled: true);
            registry.SynchronizeConfiguration(
                configuration: CreateSnapshot(
                    2,
                    CreateService(RegistrySecondServiceId, version: 1, enabled: true, healthCheckType: ServiceHealthCheckType.Process)),
                enabledServices: new HashSet<Guid> { RegistrySecondServiceId },
                lifecycleWork: new HashSet<Guid> { RegistrySecondServiceId },
                serviceOwners: null,
                now: now.AddSeconds(6));

            releaseFirstSnapshot.TrySetResult(true);
            var delivered = new List<HostServiceRuntimeStateChange>();
            while (!delivered.Any(change =>
                       change.Kind == ExtensionServiceRuntimeStateChangeKind.Removed &&
                       change.ServiceId == RegistryFirstServiceId))
            {
                delivered.Add(await changes.Reader.ReadAsync(TestContext.Current.CancellationToken));
            }

            var latestSecondSnapshot = Assert.Single(delivered, change =>
                change.ServiceId == RegistrySecondServiceId &&
                change.Kind == ExtensionServiceRuntimeStateChangeKind.Snapshot &&
                change.Snapshot?.LifecycleState == ExtensionServiceLifecycleState.Failed);
            var removedFirstSnapshot = Assert.Single(delivered, change =>
                change.ServiceId == RegistryFirstServiceId &&
                change.Kind == ExtensionServiceRuntimeStateChangeKind.Removed);
            Assert.Contains(delivered, change =>
                change.ServiceId == RegistryFirstServiceId &&
                change.IsInitialSnapshot);
            Assert.DoesNotContain(delivered, change =>
                change.ServiceId == RegistryFirstServiceId &&
                change.Kind == ExtensionServiceRuntimeStateChangeKind.Snapshot &&
                (change.Snapshot?.LifecycleState is ExtensionServiceLifecycleState.Running or ExtensionServiceLifecycleState.Failed));
            Assert.Equal("owner-second", latestSecondSnapshot.OwnerExtensionId);
            Assert.Equal("owner-first", removedFirstSnapshot.OwnerExtensionId);
        }
        finally
        {
            releaseFirstSnapshot.TrySetResult(true);
            await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConfigurationSynchronizationPreservesRunningStateAndAccumulatesRestartCount()
    {
        await using var registry = new HostServiceRuntimeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistryFirstServiceId,
                ExtensionServiceLifecycleState.Running,
                now,
                ownerExtensionId: "owner-before"),
            serviceVersion: 1,
            enabled: true);
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistryFirstServiceId,
                ExtensionServiceLifecycleState.Running,
                now.AddSeconds(1),
                ownerExtensionId: "owner-before"),
            serviceVersion: 1,
            enabled: true,
            restartCountIncrement: 1);
        registry.Publish(
            CreateRuntimeSnapshot(
                RegistryFirstServiceId,
                ExtensionServiceLifecycleState.Running,
                now.AddSeconds(2),
                ownerExtensionId: "owner-before"),
            serviceVersion: 1,
            enabled: true,
            restartCountIncrement: 1);

        registry.SynchronizeConfiguration(
            configuration: CreateSnapshot(
                2,
                CreateService(RegistryFirstServiceId, version: 2, enabled: true, healthCheckType: ServiceHealthCheckType.Process)),
            enabledServices: new HashSet<Guid> { RegistryFirstServiceId },
            lifecycleWork: new HashSet<Guid>(),
            serviceOwners: new Dictionary<Guid, string?> { [RegistryFirstServiceId] = "owner-after" },
            now: now.AddSeconds(3));

        Assert.True(registry.TryGet(RegistryFirstServiceId, out var updated));
        Assert.Equal(ExtensionServiceLifecycleState.Running, updated.LifecycleState);
        Assert.Equal(2, updated.ConfigurationVersion);
        Assert.Equal("owner-after", updated.OwnerExtensionId);
        Assert.Equal(2, updated.RestartCount);
    }

    [Fact]
    public async Task RuntimeSubscriptionDeliveryDoesNotFlowCallerExecutionContext()
    {
        await using var registry = new HostServiceRuntimeRegistry();
        var callerContext = new AsyncLocal<string?> { Value = "request-scoped" };
        var deliveredContext = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = (IAsyncDisposable)registry.Subscribe(_ => deliveredContext.TrySetResult(callerContext.Value));

        try
        {
            registry.Publish(
                CreateRuntimeSnapshot(
                    RegistryFirstServiceId,
                    ExtensionServiceLifecycleState.Starting,
                    DateTimeOffset.UtcNow,
                    ownerExtensionId: "owner-first"),
                serviceVersion: 1,
                enabled: true);

            Assert.Null(await deliveredContext.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeSnapshotsRemainTransientWithoutPersistenceDependencies()
    {
        await using var registry = new HostServiceRuntimeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Publish(
            CreateRuntimeSnapshot(HealthFailureServiceId, ExtensionServiceLifecycleState.Starting, now),
            serviceVersion: 1,
            enabled: true);
        registry.Publish(
            CreateRuntimeSnapshot(
                HealthFailureServiceId,
                ExtensionServiceLifecycleState.Failed,
                now.AddSeconds(1),
                ExtensionServiceFailureStage.HealthProbe),
            serviceVersion: 1,
            enabled: true);

        Assert.True(registry.TryGet(HealthFailureServiceId, out var failed));
        Assert.Equal(ExtensionServiceLifecycleState.Failed, failed.LifecycleState);
        Assert.Equal(ExtensionServiceFailureStage.HealthProbe, failed.FailureStage);
        Assert.False(HasRuntimePersistenceDependency(typeof(HostServiceRuntimeRegistry)));
        Assert.False(HasRuntimePersistenceDependency(typeof(HostServiceRuntimeSnapshot)));
        Assert.False(HasRuntimePersistenceDependency(typeof(HostServiceLifecycleManager)));
    }

    private static async Task<HostServiceRuntimeSnapshot> WaitForRuntimeSnapshotChangeAsync(
        ChannelReader<HostServiceRuntimeStateChange> changes,
        Guid serviceId,
        Func<HostServiceRuntimeSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var change = await changes.ReadAsync(timeout.Token);
            if (change.ServiceId == serviceId &&
                change.Snapshot is { } snapshot &&
                predicate(snapshot))
            {
                return snapshot;
            }
        }
    }

    private static HostServiceLifecycleManager CreateManager(
        HostConfigurationSnapshotHolder holder,
        RecordingExecutor executor,
        IServiceHealthProbe probe,
        IPortLeaseStore? leaseStore = null,
        HostRuntimeState? runtimeState = null,
        HostServiceRuntimeRegistry? runtimeRegistry = null)
    {
        var runtime = runtimeState ?? new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        if (runtimeState is null)
        {
            runtime.MarkSnapshotAccepted();
        }
        return new HostServiceLifecycleManager(
            executor,
            probe,
            leaseStore ?? new RecordingLeaseStore(),
            holder,
            new HostServiceEndpointSnapshotPublisher(),
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false),
            runtimeManager: null,
            runtimeRegistry: runtimeRegistry);
    }

    private static ServiceConfiguration CreateService(
        Guid serviceId,
        long version,
        bool enabled,
        ServiceHealthCheckType healthCheckType,
        ContractRestartPolicy restartPolicy = ContractRestartPolicy.Never,
        ServiceStartMode startMode = ServiceStartMode.Lazy) =>
        new(
            serviceId,
            enabled,
            "/bin/sh",
            ImmutableArray<string>.Empty,
            "/tmp",
            ImmutableDictionary<string, string>.Empty,
            startMode,
            restartPolicy,
            new ServiceHealthCheckConfiguration(
                healthCheckType,
                healthCheckType == ServiceHealthCheckType.Http ? "/health" : null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);

    private static HostConfigurationSnapshot CreateSnapshot(long version, params ServiceConfiguration[] services) =>
        new(
            version,
            new GlobalSettingsConfiguration(
                version,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            services.ToImmutableArray(),
            default,
            default);

    private static HostServiceRuntimeSnapshot CreateRuntimeSnapshot(
        Guid serviceId,
        ExtensionServiceLifecycleState lifecycleState,
        DateTimeOffset lastUpdatedAt,
        ExtensionServiceFailureStage failureStage = ExtensionServiceFailureStage.None,
        string? ownerExtensionId = null) =>
        new(
            serviceId: serviceId,
            configurationVersion: 1,
            processId: null,
            processInstanceId: null,
            startedAt: null,
            lastUpdatedAt: lastUpdatedAt,
            lastHealthAt: null,
            lifecycleState: lifecycleState,
            healthState: ExtensionServiceHealthState.Unknown,
            ownerExtensionId: ownerExtensionId,
            failureStage: failureStage);
    private static async Task<HostServiceRuntimeSnapshot> WaitForRuntimeSnapshotAsync(
        HostServiceLifecycleManager manager,
        Guid serviceId,
        Func<HostServiceRuntimeSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (manager.TryGet(serviceId, out var snapshot) && predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task ObserveReadyHealthAsync(
        HostServiceLifecycleManager manager,
        CancellationToken cancellationToken)
    {
        var method = typeof(HostServiceLifecycleManager).GetMethod(
            "ObserveReadyHealthAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The ready-health observer was not found.");
        var task = method.Invoke(manager, new object?[] { cancellationToken }) as Task ??
            throw new InvalidOperationException("The ready-health observer did not return a task.");
        await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }


    private static bool HasRuntimePersistenceDependency(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var constructorDependencies = type.GetConstructors(flags)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);
        return constructorDependencies.Any(ReferencesRuntimePersistence) ||
            type.GetFields(flags).Any(field => ReferencesRuntimePersistence(field.FieldType));
    }

    private static bool ReferencesRuntimePersistence(Type type)
    {
        if (type.Name == "IServiceRuntimePersistence")
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } elementType && ReferencesRuntimePersistence(elementType))
        {
            return true;
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(ReferencesRuntimePersistence))
        {
            return true;
        }

        return type.GetInterfaces().Any(interfaceType => interfaceType.Name == "IServiceRuntimePersistence");
    }

    private sealed class RecordingExecutor : IProcessInstanceExecutor, IProcessLiveness
    {
        private readonly bool _rejectStart;
        private readonly Action? _onStop;
        private readonly object _gate = new();
        private readonly HashSet<ProcessInstanceId> _runningInstances = [];

        public RecordingExecutor(bool rejectStart = false, Action? onStop = null)
        {
            _rejectStart = rejectStart;
            _onStop = onStop;
        }

        public ProcessInstanceId? InstanceId { get; private set; }

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            if (_rejectStart)
            {
                return ValueTask.FromResult(new ProcessOperationResult(
                    ProcessOperationStatus.Rejected,
                    ServiceStateReasonCode.StartRejected));
            }

            var instanceId = new ProcessInstanceId(Guid.NewGuid());
            lock (_gate)
            {
                InstanceId = instanceId;
                _runningInstances.Add(instanceId);
            }

            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Accepted,
                ServiceStateReasonCode.StartAccepted,
                instanceId,
                startedAt: DateTimeOffset.UtcNow));
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _runningInstances.Clear();
            }

            _onStop?.Invoke();
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            ProcessInstanceId instanceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _runningInstances.Remove(instanceId);
            }

            _onStop?.Invoke();
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }

        public bool IsRunning(Guid serviceId)
        {
            lock (_gate)
            {
                return _runningInstances.Count != 0;
            }
        }

        public bool IsRunning(Guid serviceId, ProcessInstanceId instanceId)
        {
            lock (_gate)
            {
                return _runningInstances.Contains(instanceId);
            }
        }
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        private long _version;

        internal Queue<PortLeaseOperationStatus> AcquireResults { get; } = new();
        internal List<PortLeaseIntent> AcquireIntents { get; } = [];

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind != PortLeaseIntentKind.Acquire)
            {
                return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
            }

            AcquireIntents.Add(intent);
            var status = AcquireResults.Count == 0
                ? PortLeaseOperationStatus.Applied
                : AcquireResults.Dequeue();
            if (status != PortLeaseOperationStatus.Applied)
            {
                return ValueTask.FromResult(new PortLeaseOperationResult(status));
            }

            var request = intent.Request!;
            var port = request.Port == 0
                ? request.AutomaticPortRangeStart!.Value
                : request.Port;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new PortLeaseOperationResult(
                PortLeaseOperationStatus.Applied,
                new PortLease(
                    request.NodeId,
                    request.ServiceId,
                    port,
                    now,
                    now.AddMinutes(5),
                    Interlocked.Increment(ref _version))));
        }
    }

    private sealed class LeaseOwnershipConflictStore : IPortLeaseStore
    {
        private long _version;
        private int _renewalCount;

        internal int RenewalCount => Volatile.Read(ref _renewalCount);

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind == PortLeaseIntentKind.Renew)
            {
                Interlocked.Increment(ref _renewalCount);
                return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
            }

            if (intent.Kind != PortLeaseIntentKind.Acquire)
            {
                return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
            }

            var request = intent.Request!;
            var port = request.Port == 0
                ? request.AutomaticPortRangeStart!.Value
                : request.Port;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new PortLeaseOperationResult(
                PortLeaseOperationStatus.Applied,
                new PortLease(
                    request.NodeId,
                    request.ServiceId,
                    port,
                    now,
                    now.AddSeconds(10),
                    Interlocked.Increment(ref _version))));
        }
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
                1,
                target: ServiceHealthProbe.DescribeTarget(request)));
    }

    private sealed class CandidateProgressProbe : IServiceHealthProbe
    {
        internal const string CandidateFailureDetail = "candidate probe is retrying";

        private readonly TaskCompletionSource<bool> _releaseCandidateRetry =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _probeCount;

        internal TaskCompletionSource<bool> CandidateRetryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseCandidateRetry() => _releaseCandidateRetry.TrySetResult(true);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _probeCount);
            if (attempt == 2)
            {
                return new HealthObservationResult(
                    request.ServiceId,
                    HealthObservationStatus.Unavailable,
                    DateTimeOffset.UtcNow,
                    TimeSpan.Zero,
                    attempt,
                    target: ServiceHealthProbe.DescribeTarget(request),
                    errorMessage: CandidateFailureDetail);
            }

            if (attempt == 3)
            {
                CandidateRetryEntered.TrySetResult(true);
                await _releaseCandidateRetry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                attempt,
                target: ServiceHealthProbe.DescribeTarget(request));
        }
    }

    private sealed class LongDiagnosticProbe : IServiceHealthProbe
    {
        private readonly string _errorMessage;

        public LongDiagnosticProbe(string errorMessage) => _errorMessage = errorMessage;

        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Unavailable,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1,
                target: ServiceHealthProbe.DescribeTarget(request),
                errorMessage: _errorMessage));
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        private readonly HttpRequestException _exception;

        public FailingHttpHandler(HttpRequestException exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed class GatedProbe : IServiceHealthProbe
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult(true);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.TimedOut,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1,
                target: ServiceHealthProbe.DescribeTarget(request),
                errorMessage: "The health check timed out.");
        }
    }
}
