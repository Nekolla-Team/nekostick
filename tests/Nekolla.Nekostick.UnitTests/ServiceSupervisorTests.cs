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

    private static ServiceSupervisor Create(RecordingExecutor executor, RecordingLeaseStore store, RecordingProbe? probe = null, HealthRetryPolicy? healthPolicy = null, ServiceRestartPolicy restartPolicy = ServiceRestartPolicy.OnFailure)
    {
        var launch = new ProcessLaunchSpecification(ServiceId, "/bin/service", "/tmp", ImmutableArray<string>.Empty, new ProcessEnvironment(new Dictionary<string, string>()));
        var request = new ServiceHealthProbeRequest(ServiceId, new HealthCheckDefinition(ServiceHealthCheckKind.Process, TimeSpan.FromSeconds(1)));
        var leaseRequest = new PortLeaseRequest(new NodeIdentifier("node"), ServiceId, 23456, TimeSpan.FromMinutes(1));
        return new ServiceSupervisor(executor, probe ?? new RecordingProbe(HealthObservationStatus.Healthy), store, launch, request, leaseRequest, healthPolicy, restartPolicy: restartPolicy, now: Now);
    }

    private static PortLease Lease() => new(new NodeIdentifier("node"), ServiceId, 23456, Now, Now.AddMinutes(1), 1);

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public RecordingExecutor(List<string> events, ProcessOperationResult? start = null) { Events = events; StartResult = start ?? new(ProcessOperationStatus.Accepted, ServiceStateReasonCode.StartAccepted); }
        public List<string> Events { get; }
        private ProcessOperationResult StartResult { get; }
        public ValueTask<ProcessOperationResult> StartAsync(ProcessLaunchSpecification specification, CancellationToken cancellationToken = default) { Events.Add("start"); return ValueTask.FromResult(StartResult); }
        public ValueTask<ProcessOperationResult> StopAsync(Guid serviceId, TimeSpan gracePeriod, CancellationToken cancellationToken = default) { Events.Add("stop"); return ValueTask.FromResult(new ProcessOperationResult(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted)); }
    }
    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        private readonly List<string> _events; private readonly PortLease _lease;
        public RecordingLeaseStore(List<string> events, PortLease lease) { _events = events; _lease = lease; }
        public ValueTask<PortLeaseOperationResult> ApplyAsync(PortLeaseIntent intent, CancellationToken cancellationToken = default) { _events.Add(intent.Kind == PortLeaseIntentKind.Acquire ? "acquire" : "release"); return ValueTask.FromResult(intent.Kind == PortLeaseIntentKind.Acquire ? new PortLeaseOperationResult(PortLeaseOperationStatus.Applied, _lease) : new PortLeaseOperationResult(PortLeaseOperationStatus.Applied)); }
    }

    private sealed class RecordingProbe : IServiceHealthProbe
    {
        private readonly HealthObservationStatus _status;
        public RecordingProbe(HealthObservationStatus status) => _status = status;
        public ValueTask<HealthObservationResult> ProbeAsync(ServiceHealthProbeRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new HealthObservationResult(request.ServiceId, _status, Now, TimeSpan.Zero, 1));
    }
}
