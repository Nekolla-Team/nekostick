using System.Collections.Concurrent;
using System.Reflection;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceLifecycleRestartTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000041");
    private static readonly Guid DependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000042");

    private static readonly Guid ConsumerServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000043");


    [Fact]
    public async Task ConfigSwitchWaitsForDrainBeforeStoppingOldGeneration()
    {
        var tracker = new BlockingDrainTracker();
        var executor = new RecordingExecutor();
        var serviceV1 = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshotV1 = CreateSnapshot(version: 1, serviceV1);
        var harness = CreateHarness(snapshotV1, executor, new SequenceProbe(HealthObservationStatus.Healthy), tracker);

        var initial = await harness.Manager.EnsureReadyAsync(
            snapshotV1,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, initial.Status);
        var oldLease = harness.Publisher.Current[ServiceId];

        var serviceV2 = CreateService(version: 2, ContractRestartPolicy.Never);
        var snapshotV2 = CreateSnapshot(version: 2, serviceV2);
        var switching = harness.Manager.EnsureReadyAsync(
            snapshotV2,
            ServiceId,
            TestContext.Current.CancellationToken).AsTask();

        await tracker.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);
        var candidateLease = harness.Publisher.Current[ServiceId];
        Assert.Equal(35001, candidateLease.Port);
        Assert.NotEqual(oldLease.GenerationId, candidateLease.GenerationId);
        Assert.Equal(2, harness.LeaseStore.HeldLeases.Length);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == oldLease.GenerationId);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == candidateLease.GenerationId);

        tracker.Release();
        var switched = await switching;

        Assert.Equal(HostServiceReadinessStatus.Ready, switched.Status);
        Assert.Equal(1, executor.StopCount);
        Assert.Equal(ServiceId, tracker.ServiceId);
        Assert.Equal(oldLease.Port, tracker.Port);
        Assert.Equal(TimeSpan.FromSeconds(15), tracker.Timeout);
        Assert.DoesNotContain(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == oldLease.GenerationId);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == candidateLease.GenerationId);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CrashRestartReusesHeldLeaseWithoutDrainOrServiceScopedStop()
    {
        var tracker = new BlockingDrainTracker();
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Always);
        var snapshot = CreateSnapshot(version: 1, service);
        var harness = CreateHarness(snapshot, executor, new SequenceProbe(HealthObservationStatus.Healthy), tracker);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);

        var originalLease = harness.Publisher.Current[ServiceId];
        var originalPort = originalLease.Port;
        Assert.Equal(1, harness.LeaseStore.AcquireCount);
        Assert.NotNull(executor.FirstInstanceId);

        await harness.Manager.NotifyProcessExitAsync(
            ServiceId,
            executor.FirstInstanceId!.Value,
            successfulExit: false);
        await executor.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitForAsync(() =>
            harness.Publisher.Current.TryGetValue(ServiceId, out var lease) &&
            lease.Port == originalPort &&
            lease.GenerationId == originalLease.GenerationId);

        Assert.Equal(2, executor.StartCount);
        Assert.Equal(1, harness.LeaseStore.AcquireCount);
        Assert.Equal(originalLease.GenerationId, harness.Publisher.Current[ServiceId].GenerationId);
        Assert.Single(harness.LeaseStore.HeldLeases);
        Assert.Equal(0, executor.InstanceStopCount);
        Assert.Equal(0, executor.ServiceStopCount);
        Assert.False(tracker.WaitEntered.Task.IsCompleted);

        await harness.Manager.StopAsync(CancellationToken.None);
    }


    [Fact]
    public async Task CrashRestartRetiresGenerationWhenDependencyInputsAreStale()
    {
        var dependency = CreateService(
            DependencyServiceId,
            1,
            ContractRestartPolicy.Never,
            ImmutableDictionary<string, string>.Empty.Add("SHARED_VALUE", "first"));
        var consumer = CreateService(
            ConsumerServiceId,
            1,
            ContractRestartPolicy.Always,
            ImmutableDictionary<string, string>.Empty.Add(
                "REMOTE_VALUE",
                string.Concat("${SHARED_VALUE@", DependencyServiceId, "}")));
        var snapshotV1 = CreateSnapshot(1, dependency, consumer);
        var executor = new RecordingExecutor();
        var harness = CreateHarness(
            snapshotV1,
            executor,
            new SequenceProbe(HealthObservationStatus.Healthy),
            new TimeoutDrainTracker());

        try
        {
            await harness.Manager.ReconcileAsync(snapshotV1, TestContext.Current.CancellationToken);
            Assert.Equal(2, executor.StartCount);
            Assert.True(executor.TryGetInstanceId(consumer.Id, out var consumerInstanceId));

            var dependencyV2 = CreateService(
                DependencyServiceId,
                2,
                ContractRestartPolicy.Never,
                ImmutableDictionary<string, string>.Empty.Add("SHARED_VALUE", "second"));
            var snapshotV2 = CreateSnapshot(2, dependencyV2, consumer);
            Assert.True(harness.SnapshotHolder.TryReplace(snapshotV2));

            await harness.Manager.NotifyProcessExitAsync(
                consumer.Id,
                consumerInstanceId,
                successfulExit: false);
            await WaitForAsync(() => harness.Publisher.Current.ContainsKey(consumer.Id));
            Assert.True(harness.Manager.TryGet(consumer.Id, out var consumerState));
            Assert.NotEqual(consumerInstanceId, consumerState.ProcessInstanceId);
            Assert.Equal(ExtensionServiceFailureCode.None, consumerState.FailureCode);
            var consumerSpecifications = executor.StartedSpecifications
                .Where(value => value.ServiceId == consumer.Id)
                .ToArray();
            Assert.Equal(2, consumerSpecifications.Length);
            Assert.Equal("first", consumerSpecifications[0].Environment.Values["REMOTE_VALUE"]);
            Assert.Equal("second", consumerSpecifications[1].Environment.Values["REMOTE_VALUE"]);
        }
        finally
        {
            await harness.Manager.StopAsync(CancellationToken.None);
        }
    }
    [Fact]
    public async Task CrashRestartStartsFreshStartupHealthPhase()
    {
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Always);
        var snapshot = CreateSnapshot(version: 1, service);
        var probe = new CandidateWarmupProbe();
        var harness = CreateHarness(snapshot, executor, probe, new TimeoutDrainTracker());

        var initial = await harness.Manager.EnsureReadyAsync(
            snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, initial.Status);
        var originalPort = harness.Publisher.Current[ServiceId].Port;
        Assert.NotNull(executor.FirstInstanceId);

        var restarting = harness.Manager.NotifyProcessExitAsync(
            ServiceId,
            executor.FirstInstanceId!.Value,
            successfulExit: false);
        await probe.HealthyProbeEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, probe.CallCount);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);

        probe.ReleaseHealthy();
        await restarting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(originalPort, harness.Publisher.Current[ServiceId].Port);
        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CandidateWarmupKeepsOldHealthyGenerationAfterFourMisses()
    {
        var executor = new RecordingExecutor();
        var serviceV1 = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshotV1 = CreateSnapshot(version: 1, serviceV1);
        var probe = new CandidateWarmupProbe();
        var harness = CreateHarness(snapshotV1, executor, probe, new TimeoutDrainTracker());

        var initial = await harness.Manager.EnsureReadyAsync(
            snapshotV1,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, initial.Status);
        var originalLease = harness.Publisher.Current[ServiceId];
        var originalPort = originalLease.Port;

        var serviceV2 = CreateService(version: 2, ContractRestartPolicy.Never);
        var snapshotV2 = CreateSnapshot(version: 2, serviceV2);
        var switching = harness.Manager.EnsureReadyAsync(
            snapshotV2,
            ServiceId,
            TestContext.Current.CancellationToken).AsTask();
        await probe.HealthyProbeEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, probe.CallCount);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);
        Assert.Equal(originalPort, harness.Publisher.Current[ServiceId].Port);
        Assert.Equal(originalLease.GenerationId, harness.Publisher.Current[ServiceId].GenerationId);
        Assert.Equal(2, harness.LeaseStore.HeldLeases.Length);
        var candidateLease = Assert.Single(
            harness.LeaseStore.HeldLeases,
            lease => lease.GenerationId != originalLease.GenerationId);
        Assert.NotEqual(originalPort, candidateLease.Port);

        probe.ReleaseHealthy();
        var switched = await switching.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, switched.Status);
        var activeLease = harness.Publisher.Current[ServiceId];
        Assert.NotEqual(originalPort, activeLease.Port);
        Assert.NotEqual(originalLease.GenerationId, activeLease.GenerationId);
        Assert.Equal(candidateLease.GenerationId, activeLease.GenerationId);
        Assert.DoesNotContain(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == originalLease.GenerationId);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == activeLease.GenerationId);
        Assert.Equal(1, executor.StopCount);
        await harness.Manager.StopAsync(CancellationToken.None);
    }


    [Fact]
    public async Task NeverPolicyCrashReleasesHeldLeaseWithoutStopping()
    {
        var tracker = new BlockingDrainTracker();
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshot = CreateSnapshot(version: 1, service);
        var harness = CreateHarness(snapshot, executor, new SequenceProbe(HealthObservationStatus.Healthy), tracker);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);
        Assert.NotNull(executor.FirstInstanceId);

        await harness.Manager.NotifyProcessExitAsync(
            ServiceId,
            executor.FirstInstanceId!.Value,
            successfulExit: false);

        Assert.Equal(1, harness.LeaseStore.AcquireCount);
        Assert.Equal(1, harness.LeaseStore.ReleaseCount);
        Assert.Equal(0, executor.InstanceStopCount);
        Assert.Equal(0, executor.ServiceStopCount);
        Assert.False(tracker.WaitEntered.Task.IsCompleted);
        Assert.Empty(harness.Publisher.Current);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedProcessExitCleanupDoesNotBlockNewGenerationStartAndTickRetriesOldLease()
    {
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshot = CreateSnapshot(version: 1, service);
        var leaseStore = new SequencedLeaseStore(
            PortLeaseOperationStatus.Conflict,
            PortLeaseOperationStatus.NotFound);
        var harness = CreateHarness(
            snapshot,
            executor,
            new SequenceProbe(HealthObservationStatus.Healthy),
            new TimeoutDrainTracker(),
            leaseStore);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);
        var oldLease = harness.Publisher.Current[ServiceId];
        Assert.NotNull(executor.FirstInstanceId);

        await harness.Manager.NotifyProcessExitAsync(
            ServiceId,
            executor.FirstInstanceId!.Value,
            successfulExit: false);

        Assert.Equal(1, harness.LeaseStore.ReleaseCount);
        var nextGeneration = await harness.Manager.EnsureReadyAsync(
            snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, nextGeneration.Status);
        var newLease = harness.Publisher.Current[ServiceId];
        Assert.NotEqual(oldLease.GenerationId, newLease.GenerationId);
        Assert.NotEqual(oldLease.Port, newLease.Port);
        Assert.Equal(2, harness.LeaseStore.AcquireCount);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(1, harness.LeaseStore.ReleaseCount);
        Assert.Equal(2, harness.LeaseStore.HeldLeases.Length);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == oldLease.GenerationId);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == newLease.GenerationId);
        var endpointBeforeTick = harness.Publisher.Current[ServiceId];
        await harness.Manager.RenewLeasesAsync(CancellationToken.None);

        Assert.Equal(2, harness.LeaseStore.ReleaseCount);
        var heldAfterTick = Assert.Single(harness.LeaseStore.HeldLeases);
        Assert.Equal(newLease.GenerationId, heldAfterTick.GenerationId);
        Assert.Equal(newLease.Port, heldAfterTick.Port);
        var endpointAfterTick = harness.Publisher.Current[ServiceId];
        Assert.Equal(endpointBeforeTick.GenerationId, endpointAfterTick.GenerationId);
        Assert.Equal(endpointBeforeTick.Port, endpointAfterTick.Port);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TerminalHealthPublishesHealthyCandidateBeforeStoppingOldGeneration()
    {
        var tracker = new BlockingDrainTracker();
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Always);
        var snapshot = CreateSnapshot(version: 1, service);
        var probe = new SequenceProbe(
            HealthObservationStatus.Healthy,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Healthy);
        var harness = CreateHarness(snapshot, executor, probe, tracker);

        var initial = await harness.Manager.EnsureReadyAsync(
            snapshot,
            ServiceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, initial.Status);

        await ObserveReadyHealthAsync(harness.Manager);
        await ObserveReadyHealthAsync(harness.Manager);
        await ObserveReadyHealthAsync(harness.Manager);

        await executor.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await tracker.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);
        Assert.Equal(35001, harness.Publisher.Current[ServiceId].Port);

        tracker.Release();
        await executor.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, executor.StopCount);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TerminalHealthNeverPolicyDrainsOffTickBeforeStopping()
    {
        var tracker = new BlockingDrainTracker();
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshot = CreateSnapshot(version: 1, service);
        var probe = new SequenceProbe(
            HealthObservationStatus.Healthy,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Unavailable);
        var harness = CreateHarness(snapshot, executor, probe, tracker);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);

        await ObserveReadyHealthAsync(harness.Manager).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await ObserveReadyHealthAsync(harness.Manager).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await ObserveReadyHealthAsync(harness.Manager).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await tracker.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, executor.StopCount);

        tracker.Release();
        await executor.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, executor.StopCount);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TerminalExitDuringBlueGreenDoesNotDrainOrStopDeadOldGeneration()
    {
        var tracker = new BlockingDrainTracker();
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Always);
        var snapshot = CreateSnapshot(version: 1, service);
        var probe = new SequenceProbe(
            HealthObservationStatus.Healthy,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Unavailable,
            HealthObservationStatus.Healthy);
        var harness = CreateHarness(snapshot, executor, probe, tracker);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);

        await ObserveReadyHealthAsync(harness.Manager);
        await ObserveReadyHealthAsync(harness.Manager);
        await ObserveReadyHealthAsync(harness.Manager);

        Assert.NotNull(executor.FirstInstanceId);
        await harness.Manager.NotifyProcessExitAsync(
            ServiceId,
            executor.FirstInstanceId!.Value,
            successfulExit: false);
        await executor.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);
        Assert.False(tracker.WaitEntered.Task.IsCompleted);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainTimeoutStillStopsOldGenerationWithStopGracePeriod()
    {
        var tracker = new TimeoutDrainTracker();
        var executor = new RecordingExecutor();
        var serviceV1 = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshotV1 = CreateSnapshot(version: 1, serviceV1);
        var harness = CreateHarness(snapshotV1, executor, new SequenceProbe(HealthObservationStatus.Healthy), tracker);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshotV1,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);

        var serviceV2 = CreateService(version: 2, ContractRestartPolicy.Never);
        var snapshotV2 = CreateSnapshot(version: 2, serviceV2);
        var switched = await harness.Manager.EnsureReadyAsync(
            snapshotV2,
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, switched.Status);
        Assert.Equal(1, executor.StopCount);
        Assert.Equal(TimeSpan.FromSeconds(15), tracker.Timeout);
        Assert.Equal(ServiceId, tracker.ServiceId);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RestartStartsNewGenerationWhenOldLeaseReleaseIsPending()
    {
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshot = CreateSnapshot(version: 1, service);
        var leaseStore = new SequencedLeaseStore(PortLeaseOperationStatus.Conflict);
        var harness = CreateHarness(
            snapshot,
            executor,
            new SequenceProbe(HealthObservationStatus.Healthy),
            new TimeoutDrainTracker(),
            leaseStore);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);
        var oldLease = harness.Publisher.Current[ServiceId];

        var restart = await harness.Manager.RestartAsync(
            ServiceId,
            TestContext.Current.CancellationToken);

        Assert.False(restart.IsSuccess);
        Assert.Contains("previous generation's port lease release remains unresolved", restart.Errors[0].Message);
        Assert.Equal(2, harness.LeaseStore.AcquireCount);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(1, harness.LeaseStore.ReleaseCount);
        var newLease = harness.Publisher.Current[ServiceId];
        Assert.NotEqual(oldLease.GenerationId, newLease.GenerationId);
        Assert.NotEqual(oldLease.Port, newLease.Port);
        Assert.Equal(2, harness.LeaseStore.HeldLeases.Length);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == oldLease.GenerationId);
        Assert.Contains(harness.LeaseStore.HeldLeases, lease => lease.GenerationId == newLease.GenerationId);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedStopRetriesPendingLeaseOnceAndRemovesHostMirrorAfterSuccess()
    {
        var executor = new RecordingExecutor();
        var service = CreateService(version: 1, ContractRestartPolicy.Never);
        var snapshot = CreateSnapshot(version: 1, service);
        var leaseStore = new SequencedLeaseStore(
            PortLeaseOperationStatus.Conflict,
            PortLeaseOperationStatus.Applied);
        var harness = CreateHarness(
            snapshot,
            executor,
            new SequenceProbe(HealthObservationStatus.Healthy),
            new TimeoutDrainTracker(),
            leaseStore);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await harness.Manager.EnsureReadyAsync(
                snapshot,
                ServiceId,
                TestContext.Current.CancellationToken)).Status);

        var slotsField = typeof(HostServiceLifecycleManager).GetField(
            "_slots",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(slotsField);
        var slots = slotsField!.GetValue(harness.Manager);
        Assert.NotNull(slots);
        var tryGetValue = slots!.GetType().GetMethod("TryGetValue");
        Assert.NotNull(tryGetValue);
        object?[] slotArguments = [ServiceId, null];
        Assert.True((bool)tryGetValue!.Invoke(slots, slotArguments)!);
        var slot = slotArguments[1]!;
        var activeField = slot.GetType().GetField("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activeField);
        var generation = activeField!.GetValue(slot)!;
        var stopMethod = typeof(HostServiceLifecycleManager).GetMethod(
            "StopGenerationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stopMethod);
        Task<SupervisorOperationResult> StopGenerationAsync() =>
            Assert.IsAssignableFrom<Task<SupervisorOperationResult>>(
                stopMethod!.Invoke(harness.Manager, [slot, generation, CancellationToken.None]));

        var firstStop = await StopGenerationAsync();
        Assert.Equal(SupervisorOperationStatus.Conflict, firstStop.Status);
        Assert.True(firstStop.ProcessStopped);
        Assert.Equal(1, harness.LeaseStore.ReleaseCount);

        var secondStop = await StopGenerationAsync();
        Assert.Equal(SupervisorOperationStatus.Applied, secondStop.Status);
        Assert.True(secondStop.ProcessStopped);
        Assert.Equal(2, harness.LeaseStore.ReleaseCount);

        var restarted = await harness.Manager.RestartAsync(ServiceId, TestContext.Current.CancellationToken);
        Assert.True(restarted.IsSuccess);
        Assert.Equal(2, harness.LeaseStore.ReleaseCount);

        await harness.Manager.StopAsync(CancellationToken.None);
    }

    private static Harness CreateHarness(
        HostConfigurationSnapshot snapshot,
        RecordingExecutor executor,
        IServiceHealthProbe probe,
        IMicroserviceDrainTracker tracker,
        SequencedLeaseStore? leaseStore = null)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtime.MarkSnapshotAccepted();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        leaseStore ??= new SequencedLeaseStore();
        var manager = new HostServiceLifecycleManager(
            executor,
            probe,
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            tracker,
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));
        return new Harness(manager, publisher, leaseStore, holder);
    }

    private static async Task ObserveReadyHealthAsync(HostServiceLifecycleManager manager)
    {
        var method = typeof(HostServiceLifecycleManager).GetMethod(
            "ObserveReadyHealthAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(manager, [CancellationToken.None]));
        await task;
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        params ServiceConfiguration[] services) =>
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

    private static ServiceConfiguration CreateService(
        long version,
        ContractRestartPolicy restartPolicy) =>
        CreateService(
            ServiceId,
            version,
            restartPolicy,
            ImmutableDictionary<string, string>.Empty);

    private static ServiceConfiguration CreateService(
        Guid serviceId,
        long version,
        ContractRestartPolicy restartPolicy,
        ImmutableDictionary<string, string> environment) =>
        new(
            serviceId,
            enabled: true,
            fileName: "/bin/sh",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            environment: environment,
            startMode: ServiceStartMode.Eager,
            restartPolicy: restartPolicy,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: version);

    private sealed record Harness(
        HostServiceLifecycleManager Manager,
        HostServiceEndpointSnapshotPublisher Publisher,
        SequencedLeaseStore LeaseStore,
        HostConfigurationSnapshotHolder SnapshotHolder);

    private sealed class RecordingExecutor : IProcessInstanceExecutor, IProcessLiveness
    {
        private int _startCount;
        private readonly ConcurrentDictionary<Guid, ProcessInstanceId> _instanceIds = new();

        private int _stopCount;
        private int _instanceStopCount;
        private int _serviceStopCount;

        public int StartCount => Volatile.Read(ref _startCount);
        public int StopCount => Volatile.Read(ref _stopCount);
        public int InstanceStopCount => Volatile.Read(ref _instanceStopCount);
        public int ServiceStopCount => Volatile.Read(ref _serviceStopCount);

        public ProcessInstanceId? FirstInstanceId { get; private set; }
        public ConcurrentQueue<ProcessLaunchSpecification> StartedSpecifications { get; } = new();

        public bool TryGetInstanceId(Guid serviceId, out ProcessInstanceId instanceId) =>
            _instanceIds.TryGetValue(serviceId, out instanceId);

        public TaskCompletionSource<bool> SecondStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _startCount);
            var instanceId = new ProcessInstanceId(Guid.NewGuid());
            _instanceIds[specification.ServiceId] = instanceId;

            StartedSpecifications.Enqueue(specification);
            if (count == 1)
            {
                FirstInstanceId = instanceId;
            }
            else if (count == 2)
            {
                SecondStart.TrySetResult(true);
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
            CancellationToken cancellationToken = default) =>
            RecordStop(serviceScoped: true);

        public ValueTask<ProcessOperationResult> StopAsync(
            ProcessInstanceId instanceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            RecordStop(serviceScoped: false);

        private ValueTask<ProcessOperationResult> RecordStop(bool serviceScoped)
        {
            Interlocked.Increment(ref _stopCount);
            if (serviceScoped)
            {
                Interlocked.Increment(ref _serviceStopCount);
            }
            else
            {
                Interlocked.Increment(ref _instanceStopCount);
            }

            StopEntered.TrySetResult(true);
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }
        bool IProcessLiveness.IsRunning(Guid serviceId) => true;

        bool IProcessLiveness.IsRunning(Guid serviceId, ProcessInstanceId instanceId) => true;
    }

    private sealed class SequenceProbe : IServiceHealthProbe
    {
        private readonly ConcurrentQueue<HealthObservationStatus> _statuses;
        private readonly HealthObservationStatus _fallback;

        public SequenceProbe(params HealthObservationStatus[] statuses)
        {
            _statuses = new ConcurrentQueue<HealthObservationStatus>(statuses);
            _fallback = statuses[^1];
        }

        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            var hasStatus = _statuses.TryDequeue(out var status);
            if (!hasStatus)
            {
                status = _fallback;
            }

            return ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                status,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1));
        }
    }

    private sealed class CandidateWarmupProbe : IServiceHealthProbe
    {
        private readonly TaskCompletionSource<bool> _releaseHealthy =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource<bool> HealthyProbeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            HealthObservationStatus status;
            if (call == 1)
            {
                status = HealthObservationStatus.Healthy;
            }
            else if (call <= 5)
            {
                status = HealthObservationStatus.Unavailable;
            }
            else if (call == 6)
            {
                HealthyProbeEntered.TrySetResult(true);
                await _releaseHealthy.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                status = HealthObservationStatus.Healthy;
            }
            else
            {
                status = HealthObservationStatus.Healthy;
            }

            return new HealthObservationResult(
                request.ServiceId,
                status,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                call);
        }

        public void ReleaseHealthy() => _releaseHealthy.TrySetResult(true);
    }

    private sealed class SequencedLeaseStore : IPortLeaseStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<(Guid ServiceId, Guid GenerationId), PortLease> _leases = new();
        private readonly Dictionary<(NodeIdentifier NodeId, int Port), (Guid ServiceId, Guid GenerationId)> _ports = new();
        private readonly ConcurrentQueue<PortLeaseOperationStatus> _releaseStatuses;
        private long _nextVersion;
        private int _acquireCount;
        private int _releaseCount;

        public SequencedLeaseStore(params PortLeaseOperationStatus[] releaseStatuses)
        {
            _releaseStatuses = new ConcurrentQueue<PortLeaseOperationStatus>(releaseStatuses);
        }

        public int AcquireCount => Volatile.Read(ref _acquireCount);
        public int ReleaseCount => Volatile.Read(ref _releaseCount);
        public PortLease[] HeldLeases
        {
            get
            {
                lock (_gate)
                {
                    return _leases.Values.ToArray();
                }
            }
        }

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (intent.Kind == PortLeaseIntentKind.Acquire)
                {
                    Interlocked.Increment(ref _acquireCount);
                    var request = intent.Request!;
                    var leaseKey = (request.ServiceId, request.GenerationId);
                    if (_leases.ContainsKey(leaseKey))
                    {
                        return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                    }

                    var port = request.Port;
                    if (port == 0)
                    {
                        var rangeEnd = request.AutomaticPortRangeEnd ?? 35099;
                        port = request.AutomaticPortRangeStart ?? 35000;
                        while (port <= rangeEnd && _ports.ContainsKey((request.NodeId, port)))
                        {
                            port++;
                        }

                        if (port > rangeEnd)
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                        }
                    }

                    var portKey = (request.NodeId, port);
                    if (_ports.ContainsKey(portKey))
                    {
                        return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                    }

                    var now = DateTimeOffset.UtcNow;
                    var lease = new PortLease(
                        request.NodeId,
                        request.ServiceId,
                        request.GenerationId,
                        port,
                        now,
                        now.Add(request.TimeToLive),
                        Interlocked.Increment(ref _nextVersion));
                    _leases.Add(leaseKey, lease);
                    _ports.Add(portKey, leaseKey);
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Applied,
                        lease));
                }

                if (intent.Kind == PortLeaseIntentKind.Release)
                {
                    Interlocked.Increment(ref _releaseCount);
                    var release = intent.Release!;
                    var status = _releaseStatuses.TryDequeue(out var nextStatus)
                        ? nextStatus
                        : PortLeaseOperationStatus.NotFound;
                    var leaseKey = (release.ServiceId, release.GenerationId);
                    if (_leases.TryGetValue(leaseKey, out var heldLease) &&
                        heldLease.NodeId == release.NodeId &&
                        heldLease.Port == release.Port &&
                        heldLease.Version == release.LeaseVersion &&
                        status is PortLeaseOperationStatus.Applied or PortLeaseOperationStatus.NotFound)
                    {
                        _leases.Remove(leaseKey);
                        _ports.Remove((heldLease.NodeId, heldLease.Port));
                        if (status == PortLeaseOperationStatus.Applied)
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(status, heldLease));
                        }
                    }

                    if (status == PortLeaseOperationStatus.Applied)
                    {
                        status = PortLeaseOperationStatus.NotFound;
                    }

                    return ValueTask.FromResult(new PortLeaseOperationResult(status));
                }

                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.NotFound));
            }
        }
    }

    private sealed class BlockingDrainTracker : IMicroserviceDrainTracker
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid ServiceId { get; private set; }
        public int Port { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public TaskCompletionSource<bool> WaitEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable BeginTracking(Guid serviceId, int port) => EmptyDisposable.Instance;

        public async ValueTask WaitDrainedAsync(
            Guid serviceId,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ServiceId = serviceId;
            Port = port;
            Timeout = timeout;
            WaitEntered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class TimeoutDrainTracker : IMicroserviceDrainTracker
    {
        public Guid ServiceId { get; private set; }
        public TimeSpan Timeout { get; private set; }

        public IDisposable BeginTracking(Guid serviceId, int port) => EmptyDisposable.Instance;

        public ValueTask WaitDrainedAsync(
            Guid serviceId,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ServiceId = serviceId;
            Timeout = timeout;
            // This completed task models the contract's normal timeout completion without sleeping 15 seconds.
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
