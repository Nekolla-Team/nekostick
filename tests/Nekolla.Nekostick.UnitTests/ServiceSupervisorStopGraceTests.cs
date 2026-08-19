using System.Collections.Immutable;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServiceSupervisorStopGraceTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000031");
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OmittedStopGraceUsesFifteenSecondRuntimeDefault()
    {
        var executor = new RecordingExecutor();
        await StopAsync(executor);

        Assert.Equal(TimeSpan.FromSeconds(15), executor.LastGracePeriod);
    }

    [Fact]
    public async Task ExplicitStopGracePeriodIsPropagatedUnchanged()
    {
        var executor = new RecordingExecutor();
        await StopAsync(executor, TimeSpan.FromSeconds(4.5));

        Assert.Equal(TimeSpan.FromSeconds(4.5), executor.LastGracePeriod);
    }

    private static async Task StopAsync(RecordingExecutor executor, TimeSpan? stopGracePeriod = null)
    {
        await using var supervisor = new ServiceSupervisor(
            executor,
            new HealthyProbe(),
            new RecordingLeaseStore(Lease()),
            new ProcessLaunchSpecification(
                ServiceId,
                "/bin/service",
                "/tmp",
                ImmutableArray<string>.Empty,
                new ProcessEnvironment(new Dictionary<string, string>())),
            new ServiceHealthProbeRequest(
                ServiceId,
                new HealthCheckDefinition(ServiceHealthCheckKind.Process, TimeSpan.FromSeconds(1))),
            new PortLeaseRequest(new NodeIdentifier("node"), ServiceId, 23456, TimeSpan.FromMinutes(1)),
            stopGracePeriod: stopGracePeriod,
            now: Now,
            initialLease: Lease());

        var started = await supervisor.StartAsync(Now);
        Assert.Equal(SupervisorOperationStatus.Applied, started.Status);

        var stopped = await supervisor.StopAsync(Now.AddSeconds(1));
        Assert.Equal(SupervisorOperationStatus.Applied, stopped.Status);
    }

    private static PortLease Lease() =>
        new(new NodeIdentifier("node"), ServiceId, 23456, Now, Now.AddMinutes(1), 1);

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public TimeSpan? LastGracePeriod { get; private set; }

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Accepted,
                ServiceStateReasonCode.StartAccepted));

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            LastGracePeriod = gracePeriod;
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        private readonly PortLease lease;

        public RecordingLeaseStore(PortLease lease) => this.lease = lease;

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PortLeaseOperationResult(
                PortLeaseOperationStatus.Applied,
                lease));
    }

    private sealed class HealthyProbe : IServiceHealthProbe
    {
        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                Now,
                TimeSpan.Zero,
                1));
    }
}
