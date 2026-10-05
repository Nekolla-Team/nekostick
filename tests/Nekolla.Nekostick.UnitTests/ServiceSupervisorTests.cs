using System.Collections.Immutable;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServiceSupervisorTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] AcquireThenStart = ["acquire", "start"];
    private static readonly string[] AcquireStartThenRelease = ["acquire", "start", "release"];
    private static readonly string[] StopThenRelease = ["stop", "release"];

    [Fact]
    public async Task StartAcquiresLeaseBeforeStartingProcess()
    {
        var events = new List<string>();
        var lease = Lease();
        var supervisor = Create(new RecordingExecutor(events), new RecordingLeaseStore(events, lease));

        var result = await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Applied, result.Status);
        Assert.Equal(AcquireThenStart, events);
        Assert.Same(lease, supervisor.Lease);
    }

    [Fact]
    public async Task RejectedStartReleasesLease()
    {
        var events = new List<string>();
        var supervisor = Create(new RecordingExecutor(events, new ProcessOperationResult(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StartRejected)), new RecordingLeaseStore(events, Lease()));

        var result = await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Rejected, result.Status);
        Assert.Equal(AcquireStartThenRelease, events);
        Assert.Null(supervisor.Lease);
    }

    [Fact]
    public async Task StopReleasesLeaseAfterProcessStop()
    {
        var events = new List<string>();
        var supervisor = Create(new RecordingExecutor(events), new RecordingLeaseStore(events, Lease()));
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);
        events.Clear();

        var result = await supervisor.StopAsync(Now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Applied, result.Status);
        Assert.Equal(StopThenRelease, events);
        Assert.Null(supervisor.Lease);
    }

    [Theory]
    [InlineData(PortLeaseOperationStatus.Conflict, SupervisorOperationStatus.Conflict, ServiceStateReasonCode.PortLeaseConflict)]
    [InlineData(PortLeaseOperationStatus.DatabaseUnavailable, SupervisorOperationStatus.Unavailable, ServiceStateReasonCode.DatabaseUnavailable)]
    [InlineData(PortLeaseOperationStatus.Cancelled, SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled)]
    [InlineData(PortLeaseOperationStatus.Rejected, SupervisorOperationStatus.Rejected, ServiceStateReasonCode.PortLeaseUnavailable)]
    public async Task FailedReleaseRetainsLeaseWhileReportingStoppedProcess(
        PortLeaseOperationStatus releaseStatus,
        SupervisorOperationStatus expectedStatus,
        ServiceStateReasonCode expectedReason)
    {
        var events = new List<string>();
        var lease = Lease();
        var store = new RecordingLeaseStore(
            events,
            lease,
            releaseResult: () => new PortLeaseOperationResult(releaseStatus));
        var supervisor = Create(new RecordingExecutor(events), store);

        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);
        var result = await supervisor.StopAsync(Now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.True(result.ProcessStopped);
        Assert.Null(supervisor.Lease);
        Assert.Same(lease, supervisor.PendingLeaseRelease);
        Assert.Same(result.Snapshot, supervisor.Snapshot);
        var blockedStart = await supervisor.StartAsync(Now.AddSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, blockedStart.Status);
        Assert.Equal(expectedReason, blockedStart.Reason);
        Assert.Same(lease, supervisor.PendingLeaseRelease);
        Assert.Equal(["acquire", "start", "stop", "release", "release"], events);
    }

    [Fact]
    public async Task ProcessExitAcknowledgementReportsPendingReleaseFailure()
    {
        var events = new List<string>();
        var lease = Lease();
        var store = new RecordingLeaseStore(
            events,
            lease,
            releaseResult: () => new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
        var supervisor = Create(new RecordingExecutor(events), store);
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);

        var acknowledged = await supervisor.AcknowledgeProcessExitAsync();

        Assert.Equal(SupervisorOperationStatus.Conflict, acknowledged.Status);
        Assert.True(acknowledged.ProcessStopped);
        Assert.Null(supervisor.Lease);
        Assert.Same(lease, supervisor.PendingLeaseRelease);
        Assert.Equal(["acquire", "start", "release"], events);
    }

    [Fact]
    public async Task PendingReleaseRetriesBeforeStartAndNotFoundClearsIt()
    {
        var events = new List<string>();
        var lease = Lease();
        var releaseAttempts = 0;
        PortLeaseOperationResult Release()
        {
            return Interlocked.Increment(ref releaseAttempts) == 1
                ? new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict)
                : new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound);
        }

        var store = new RecordingLeaseStore(events, lease, releaseResult: Release);
        var supervisor = Create(new RecordingExecutor(events), store);
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);

        var stopped = await supervisor.StopAsync(Now.AddSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(SupervisorOperationStatus.Conflict, stopped.Status);
        Assert.Null(supervisor.Lease);

        var restarted = await supervisor.StartAsync(Now.AddSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Applied, restarted.Status);
        Assert.Equal(2, releaseAttempts);
        Assert.NotNull(supervisor.Lease);
        Assert.Null(supervisor.PendingLeaseRelease);
        Assert.Equal(["acquire", "start", "stop", "release", "release", "acquire", "start"], events);
    }

    [Fact]
    public async Task ReleaseExceptionIsRetainedAndRetriedBeforeStart()
    {
        var events = new List<string>();
        var lease = Lease();
        var releaseAttempts = 0;
        PortLeaseOperationResult Release()
        {
            if (Interlocked.Increment(ref releaseAttempts) == 1)
            {
                throw new InvalidOperationException("release failure");
            }

            return new PortLeaseOperationResult(PortLeaseOperationStatus.Applied, lease);
        }

        var store = new RecordingLeaseStore(events, lease, releaseResult: Release);
        var supervisor = Create(new RecordingExecutor(events), store);
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);

        var stopped = await supervisor.StopAsync(Now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Failed, stopped.Status);
        Assert.Equal(ServiceStateReasonCode.DatabaseUnavailable, stopped.Reason);
        Assert.True(stopped.ProcessStopped);
        Assert.Null(supervisor.Lease);
        Assert.Same(lease, supervisor.PendingLeaseRelease);

        var restarted = await supervisor.StartAsync(Now.AddSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Applied, restarted.Status);
        Assert.Equal(2, releaseAttempts);
        Assert.NotNull(supervisor.Lease);
        Assert.Null(supervisor.PendingLeaseRelease);
    }

    [Fact]
    public async Task ExpiredPendingReleaseDoesNotBlockLeaseReacquisition()
    {
        var events = new List<string>();
        var lease = Lease();
        var store = new RecordingLeaseStore(
            events,
            lease,
            acquireLease: count => count == 1
                ? lease
                : Lease(Now.AddMinutes(2), Now.AddMinutes(3), 2),
            releaseResult: () => new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
        var supervisor = Create(new RecordingExecutor(events), store);
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);

        var stopped = await supervisor.StopAsync(Now.AddSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(SupervisorOperationStatus.Conflict, stopped.Status);

        var restarted = await supervisor.StartAsync(Now.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Applied, restarted.Status);
        Assert.NotNull(supervisor.Lease);
        Assert.Null(supervisor.PendingLeaseRelease);
        Assert.Equal(["acquire", "start", "stop", "release", "acquire", "start"], events);
    }

    [Fact]
    public async Task DeadTrackedIdentityStopReleasesLeaseWithoutServiceScopedStop()
    {
        var events = new List<string>();
        var executor = new DeadLivenessExecutor();
        var supervisor = Create(executor, new RecordingLeaseStore(events, Lease()));

        var started = await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);
        Assert.Equal(SupervisorOperationStatus.Applied, started.Status);

        events.Clear();
        var stopped = await supervisor.StopAsync(Now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(SupervisorOperationStatus.Applied, stopped.Status);
        Assert.Equal(ServiceStateReasonCode.StopCompleted, stopped.Reason);
        Assert.Equal(["release"], events);
        Assert.Equal(0, executor.InstanceStopCalls);
        Assert.Equal(0, executor.ServiceStopCalls);
        Assert.Null(supervisor.Lease);
    }

    [Theory]
    [InlineData(ProcessOperationStatus.Rejected, SupervisorOperationStatus.Failed, ServiceStateReasonCode.StopRequested)]
    [InlineData(ProcessOperationStatus.Failed, SupervisorOperationStatus.Failed, ServiceStateReasonCode.StopRequested)]
    [InlineData(ProcessOperationStatus.Cancelled, SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled)]
    public async Task IncompleteStopRetainsLeaseAndStoppingSnapshot(
        ProcessOperationStatus processStatus,
        SupervisorOperationStatus supervisorStatus,
        ServiceStateReasonCode reason)
    {
        var events = new List<string>();
        var lease = Lease();
        var supervisor = Create(
            new RecordingExecutor(events, stop: new ProcessOperationResult(processStatus, reason)),
            new RecordingLeaseStore(events, lease));
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);
        var publishedLease = supervisor.Lease;
        events.Clear();

        var result = await supervisor.StopAsync(Now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(supervisorStatus, result.Status);
        Assert.False(result.ProcessStopped);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(["stop"], events);
        Assert.NotNull(publishedLease);
        Assert.Same(publishedLease, supervisor.Lease);
        Assert.Equal(ServiceLifecycleState.Stopping, result.Snapshot.ObservedLifecycle);
        Assert.Equal(ServiceStateReasonCode.StopRequested, result.Snapshot.Reason);
        Assert.Same(result.Snapshot, supervisor.Snapshot);
    }


    [Fact]
    public async Task CancelledStartMapsToCancelledWithoutStartingWhenAlreadyRequested()
    {
        var executor = new RecordingExecutor(new List<string>());
        var store = new RecordingLeaseStore(new List<string>(), Lease());
        var supervisor = Create(executor, store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await supervisor.StartAsync(Now, cancellation.Token);

        Assert.Equal(SupervisorOperationStatus.Cancelled, result.Status);
        Assert.Equal(ServiceStateReasonCode.Cancelled, result.Reason);
        Assert.Empty(executor.Events);
    }

    [Fact]
    public async Task HealthFailureMapsToRetryThenThreshold()
    {
        var probe = new RecordingProbe(HealthObservationStatus.Unhealthy);
        var policy = new HealthRetryPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 2);
        var supervisor = Create(new RecordingExecutor(new List<string>()), new RecordingLeaseStore(new List<string>(), Lease()), probe, policy);
        await supervisor.StartAsync(Now, TestContext.Current.CancellationToken);
        var state = HealthRetryState.Start(ServiceId, Now, policy.StartupTimeout);

        var first = await supervisor.ObserveHealthAsync(
            state,
            Now,
            TestContext.Current.CancellationToken);
        var second = await supervisor.ObserveHealthAsync(
            first.Health!.NextState,
            Now.AddSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthRetryAction.Retry, first.Health!.Action);
        Assert.Equal(ServiceStateReasonCode.HealthCheckFailed, first.Reason);
        Assert.Equal(HealthRetryAction.Failed, second.Health!.Action);
        Assert.Equal(ServiceStateReasonCode.HealthFailureThreshold, second.Reason);
    }

    [Theory]
    [InlineData(ServiceRestartPolicy.Never, false, ServiceStateReasonCode.RestartPolicyDisabled)]
    [InlineData(ServiceRestartPolicy.OnFailure, false, ServiceStateReasonCode.RestartPolicyDisabled)]
    [InlineData(ServiceRestartPolicy.OnFailure, true, ServiceStateReasonCode.ProcessExited)]
    [InlineData(ServiceRestartPolicy.Always, false, ServiceStateReasonCode.ProcessExited)]
    public void RestartPolicyProducesExpectedPlan(ServiceRestartPolicy policy, bool failed, ServiceStateReasonCode reason)
    {
        var supervisor = Create(new RecordingExecutor(new List<string>()), new RecordingLeaseStore(new List<string>(), Lease()), restartPolicy: policy);
        if (failed || policy == ServiceRestartPolicy.Always)
        {
            supervisor.SetDesiredState(DesiredServiceState.Running, Now);
        }

        var result = supervisor.RecordProcessExit(!failed, Now, TestContext.Current.CancellationToken);

        Assert.Equal(reason, result.Reason);
        Assert.Equal(failed || policy == ServiceRestartPolicy.Always, result.Restart!.ShouldRestart);
    }

    private static ServiceSupervisor Create(IProcessExecutor executor, RecordingLeaseStore store, RecordingProbe? probe = null, HealthRetryPolicy? healthPolicy = null, ServiceRestartPolicy restartPolicy = ServiceRestartPolicy.OnFailure)
    {
        var launch = new ProcessLaunchSpecification(ServiceId, "/bin/sh", "/tmp", ImmutableArray<string>.Empty, new ProcessEnvironment(new Dictionary<string, string>()));
        var request = new ServiceHealthProbeRequest(ServiceId, new HealthCheckDefinition(ServiceHealthCheckKind.Process, TimeSpan.FromSeconds(1)));
        var leaseRequest = new PortLeaseRequest(new NodeIdentifier("node"), ServiceId, 23456, TimeSpan.FromMinutes(1));
        return new ServiceSupervisor(executor, probe ?? new RecordingProbe(HealthObservationStatus.Healthy), store, launch, request, leaseRequest, healthPolicy, restartPolicy: restartPolicy, now: Now);
    }

    private static PortLease Lease() => Lease(Now, Now.AddMinutes(1), 1);

    private static PortLease Lease(DateTimeOffset acquiredAt, DateTimeOffset expiresAt, long version) =>
        new(new NodeIdentifier("node"), ServiceId, 23456, acquiredAt, expiresAt, version);

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public RecordingExecutor(
            List<string> events,
            ProcessOperationResult? start = null,
            ProcessOperationResult? stop = null)
        {
            Events = events;
            StartResult = start ?? new(ProcessOperationStatus.Accepted, ServiceStateReasonCode.StartAccepted);
            StopResult = stop ?? new(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted);
        }

        public List<string> Events { get; }
        private ProcessOperationResult StartResult { get; }
        private ProcessOperationResult StopResult { get; }

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            Events.Add("start");
            return ValueTask.FromResult(StartResult);
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            Events.Add("stop");
            return ValueTask.FromResult(StopResult);
        }
    }
    private sealed class DeadLivenessExecutor : IProcessInstanceExecutor, IProcessLiveness
    {
        public int InstanceStopCalls { get; private set; }
        public int ServiceStopCalls { get; private set; }

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Accepted,
                ServiceStateReasonCode.StartAccepted,
                new ProcessInstanceId(Guid.NewGuid()),
                startedAt: DateTimeOffset.UtcNow));

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            ServiceStopCalls++;
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            ProcessInstanceId instanceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            InstanceStopCalls++;
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }

        bool IProcessLiveness.IsRunning(Guid serviceId) => false;

        bool IProcessLiveness.IsRunning(Guid serviceId, ProcessInstanceId instanceId) => false;
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        private readonly List<string> _events;
        private readonly PortLease _lease;
        private readonly Func<int, PortLease>? _acquireLease;
        private readonly Func<PortLeaseOperationResult>? _releaseResult;
        private int _acquireCount;

        public RecordingLeaseStore(
            List<string> events,
            PortLease lease,
            Func<int, PortLease>? acquireLease = null,
            Func<PortLeaseOperationResult>? releaseResult = null)
        {
            _events = events;
            _lease = lease;
            _acquireLease = acquireLease;
            _releaseResult = releaseResult;
        }

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind == PortLeaseIntentKind.Acquire)
            {
                _events.Add("acquire");
                var lease = _acquireLease?.Invoke(Interlocked.Increment(ref _acquireCount)) ?? _lease;
                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.Applied,
                    lease));
            }

            _events.Add("release");
            return ValueTask.FromResult(_releaseResult?.Invoke() ?? new PortLeaseOperationResult(
                PortLeaseOperationStatus.Applied,
                _lease));
        }
    }

    private sealed class RecordingProbe : IServiceHealthProbe
    {
        private readonly HealthObservationStatus _status;
        public RecordingProbe(HealthObservationStatus status) => _status = status;
        public ValueTask<HealthObservationResult> ProbeAsync(ServiceHealthProbeRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new HealthObservationResult(request.ServiceId, _status, Now, TimeSpan.Zero, 1));
    }
}
