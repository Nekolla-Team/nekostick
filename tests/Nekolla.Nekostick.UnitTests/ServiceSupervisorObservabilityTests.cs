using System.Collections.Immutable;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServiceSupervisorObservabilityTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000074");
    private static readonly NodeIdentifier NodeId = new("node");
    private const int Port = 18432;
    private const string ExpectedTarget = "http://127.0.0.1:18432";

    [Fact]
    public async Task MissingLeaseObservationIncludesTargetAndUnavailableDetail()
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new RecordingProbe();
        var supervisor = CreateSupervisor(probe, now);

        var result = await supervisor.ObserveHealthAsync(
            HealthRetryState.Start(ServiceId, now, TimeSpan.FromSeconds(30)),
            now,
            TestContext.Current.CancellationToken);

        var observation = Assert.IsType<HealthObservationResult>(result.Snapshot.LastHealthObservation);
        Assert.Equal(HealthObservationStatus.Unavailable, observation.Status);
        Assert.Equal(ExpectedTarget, observation.Target);
        Assert.Equal("The service health check port lease is unavailable.", observation.ErrorMessage);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task ExpiredLeaseObservationIncludesTargetAndExpirationDetail()
    {
        var now = DateTimeOffset.UtcNow;
        var expiringLease = new PortLease(
            NodeId,
            ServiceId,
            Port,
            now,
            now.AddSeconds(1),
            1);
        var probe = new RecordingProbe();
        var supervisor = CreateSupervisor(probe, now, expiringLease);

        var result = await supervisor.ObserveHealthAsync(
            HealthRetryState.Start(ServiceId, now, TimeSpan.FromSeconds(30)),
            now.AddSeconds(2),
            TestContext.Current.CancellationToken);

        var observation = Assert.IsType<HealthObservationResult>(result.Snapshot.LastHealthObservation);
        Assert.Equal(HealthObservationStatus.Unavailable, observation.Status);
        Assert.Equal(ExpectedTarget, observation.Target);
        Assert.Equal("The service health check port lease expired.", observation.ErrorMessage);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task CallerCancellationFallbackIncludesTargetAndCancellationDetail()
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new BlockingProbe();
        var supervisor = CreateSupervisor(probe, now, CreateLease(now));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = supervisor.ObserveHealthAsync(
            HealthRetryState.Start(ServiceId, now, TimeSpan.FromSeconds(30)),
            now,
            cancellation.Token).AsTask();

        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var observation = Assert.IsType<HealthObservationResult>(result.Snapshot.LastHealthObservation);
        Assert.Equal(HealthObservationStatus.Cancelled, observation.Status);
        Assert.Equal(ExpectedTarget, observation.Target);
        Assert.Equal("The health check was cancelled.", observation.ErrorMessage);
    }

    [Fact]
    public async Task ProbeExceptionFallbackIncludesTargetAndGenericFailureDetail()
    {
        const string failureDetail = "internal probe fault";
        var now = DateTimeOffset.UtcNow;
        var supervisor = CreateSupervisor(new ThrowingProbe(failureDetail), now, CreateLease(now));

        var result = await supervisor.ObserveHealthAsync(
            HealthRetryState.Start(ServiceId, now, TimeSpan.FromSeconds(30)),
            now,
            TestContext.Current.CancellationToken);

        var observation = Assert.IsType<HealthObservationResult>(result.Snapshot.LastHealthObservation);
        Assert.Equal(HealthObservationStatus.Unavailable, observation.Status);
        Assert.Equal(ExpectedTarget, observation.Target);
        Assert.Equal("The health check target is unavailable.", observation.ErrorMessage);
    }

    private static ServiceSupervisor CreateSupervisor(
        IServiceHealthProbe healthProbe,
        DateTimeOffset now,
        PortLease? initialLease = null)
    {
        var launch = new ProcessLaunchSpecification(
            ServiceId,
            "/bin/sh",
            "/tmp",
            ImmutableArray<string>.Empty,
            new ProcessEnvironment(new Dictionary<string, string>()));
        var healthRequest = new ServiceHealthProbeRequest(
            ServiceId,
            new HealthCheckDefinition(ServiceHealthCheckKind.Http, TimeSpan.FromSeconds(1), "/health"),
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, Port));
        var leaseRequest = new PortLeaseRequest(NodeId, ServiceId, Port, TimeSpan.FromMinutes(1));
        var policy = new HealthRetryPolicy(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1),
            3);

        return new ServiceSupervisor(
            new TestExecutor(),
            healthProbe,
            new NoOpLeaseStore(),
            launch,
            healthRequest,
            leaseRequest,
            policy,
            now: now,
            initialLease: initialLease);
    }

    private static PortLease CreateLease(DateTimeOffset now) =>
        new(NodeId, ServiceId, Port, now, now.AddMinutes(1), 1);

    private sealed class TestExecutor : IProcessExecutor
    {
        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Rejected,
                ServiceStateReasonCode.StartRejected));

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
    }

    private sealed class NoOpLeaseStore : IPortLeaseStore
    {
        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
    }

    private sealed class RecordingProbe : IServiceHealthProbe
    {
        public int CallCount { get; private set; }

        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1));
        }
    }

    private sealed class ThrowingProbe : IServiceHealthProbe
    {
        private readonly string _message;

        public ThrowingProbe(string message) => _message = message;

        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<HealthObservationResult>(new InvalidOperationException(_message));
    }

    private sealed class BlockingProbe : IServiceHealthProbe
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1);
        }
    }
}
