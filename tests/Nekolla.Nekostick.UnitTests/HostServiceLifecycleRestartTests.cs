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

        var serviceV2 = CreateService(version: 2, ContractRestartPolicy.Never);
        var snapshotV2 = CreateSnapshot(version: 2, serviceV2);
        var switching = harness.Manager.EnsureReadyAsync(
            snapshotV2,
            ServiceId,
            TestContext.Current.CancellationToken).AsTask();

        await tracker.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, executor.StartCount);
        Assert.Equal(0, executor.StopCount);
        Assert.Equal(35001, harness.Publisher.Current[ServiceId].Port);

        tracker.Release();
        var switched = await switching;

        Assert.Equal(HostServiceReadinessStatus.Ready, switched.Status);
        Assert.Equal(1, executor.StopCount);
        Assert.Equal(ServiceId, tracker.ServiceId);
        Assert.Equal(35000, tracker.Port);
        Assert.Equal(TimeSpan.FromSeconds(15), tracker.Timeout);

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

        var originalPort = harness.Publisher.Current[ServiceId].Port;
        Assert.Equal(1, harness.LeaseStore.AcquireCount);
        Assert.NotNull(executor.FirstInstanceId);

        await harness.Manager.NotifyProcessExitAsync(
            ServiceId,
            executor.FirstInstanceId!.Value,
            successfulExit: false);
        await executor.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitForAsync(() =>
            harness.Publisher.Current.TryGetValue(ServiceId, out var lease) && lease.Port == originalPort);

        Assert.Equal(2, executor.StartCount);
        Assert.Equal(1, harness.LeaseStore.AcquireCount);
        Assert.Equal(0, executor.InstanceStopCount);
        Assert.Equal(0, executor.ServiceStopCount);
        Assert.False(tracker.WaitEntered.Task.IsCompleted);

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

    private static Harness CreateHarness(
        HostConfigurationSnapshot snapshot,
        RecordingExecutor executor,
        SequenceProbe probe,
        IMicroserviceDrainTracker tracker)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtime.MarkSnapshotAccepted();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var leaseStore = new SequencedLeaseStore();
        var manager = new HostServiceLifecycleManager(
            executor,
            probe,
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            tracker);
        return new Harness(manager, publisher, leaseStore);
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
        ServiceConfiguration service) =>
        new(
            version,
            new GlobalSettingsConfiguration(
                version,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            ImmutableArray.Create(service),
            default,
            default);

    private static ServiceConfiguration CreateService(
        long version,
        ContractRestartPolicy restartPolicy) =>
        new(
            ServiceId,
            enabled: true,
            fileName: "/bin/service",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty,
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
        SequencedLeaseStore LeaseStore);

    private sealed class RecordingExecutor : IProcessInstanceExecutor, IProcessLiveness
    {
        private int _startCount;
        private int _stopCount;
        private int _instanceStopCount;
        private int _serviceStopCount;

        public int StartCount => Volatile.Read(ref _startCount);
        public int StopCount => Volatile.Read(ref _stopCount);
        public int InstanceStopCount => Volatile.Read(ref _instanceStopCount);
        public int ServiceStopCount => Volatile.Read(ref _serviceStopCount);

        public ProcessInstanceId? FirstInstanceId { get; private set; }
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

    private sealed class SequencedLeaseStore : IPortLeaseStore
    {
        private int _nextPort = 35000;
        private long _nextVersion;
        private int _acquireCount;
        private int _releaseCount;

        public int AcquireCount => Volatile.Read(ref _acquireCount);
        public int ReleaseCount => Volatile.Read(ref _releaseCount);
        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind == PortLeaseIntentKind.Acquire)
            {
                Interlocked.Increment(ref _acquireCount);
                var request = intent.Request!;
                var now = DateTimeOffset.UtcNow;
                var lease = new PortLease(
                    request.NodeId,
                    request.ServiceId,
                    Interlocked.Increment(ref _nextPort) - 1,
                    now,
                    now.AddMinutes(5),
                    Interlocked.Increment(ref _nextVersion));
                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.Applied,
                    lease));
            }
            if (intent.Kind == PortLeaseIntentKind.Release)
            {
                Interlocked.Increment(ref _releaseCount);
            }

            return ValueTask.FromResult(new PortLeaseOperationResult(
                PortLeaseOperationStatus.NotFound));
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
