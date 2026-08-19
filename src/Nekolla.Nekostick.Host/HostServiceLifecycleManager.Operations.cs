using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using ContractHealthKind = Nekolla.Nekostick.Contracts.ServiceHealthCheckType;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using DomainHealthKind = Nekolla.Nekostick.Domain.ServiceHealthCheckKind;
using DomainRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostServiceLifecycleManager
{
    private ServiceSupervisor CreateSupervisor(
        ServiceConfiguration service,
        int port,
        PortLease? initialLease = null,
        DateTimeOffset? initialLeaseNow = null)
    {
        var arguments = service.ArgumentList.IsDefault
            ? ImmutableArray<string>.Empty
            : service.ArgumentList.Select(value => value.Replace("$PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToImmutableArray();
        var environment = service.Environment.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        environment["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HOST"] = "127.0.0.1";
        var launch = new ProcessLaunchSpecification(
            service.Id,
            service.FileName,
            service.WorkingDirectory,
            arguments,
            new ProcessEnvironment(environment));
        var healthDefinition = new HealthCheckDefinition(
            service.HealthCheck.Type switch
            {
                ContractHealthKind.Process => DomainHealthKind.Process,
                ContractHealthKind.Tcp => DomainHealthKind.Tcp,
                ContractHealthKind.Http => DomainHealthKind.Http,
                _ => DomainHealthKind.Tcp
            },
            service.HealthCheck.Timeout,
            service.HealthCheck.HttpPath);
        var healthRequest = new ServiceHealthProbeRequest(
            service.Id,
            healthDefinition,
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, port));
        var leaseRequest = new PortLeaseRequest(
            _nodeId,
            service.Id,
            port,
            LeasePolicy.TimeToLive);
        return new ServiceSupervisor(
            _processExecutor,
            _healthProbe,
            _leaseStore,
            launch,
            healthRequest,
            leaseRequest,
            HealthPolicy,
            restartPolicy: service.RestartPolicy switch
            {
                ContractRestartPolicy.Never => DomainRestartPolicy.Never,
                ContractRestartPolicy.Always => DomainRestartPolicy.Always,
                _ => DomainRestartPolicy.OnFailure
            },
            stopGracePeriod: StopGracePeriod,
            now: initialLeaseNow,
            initialLease: initialLease);
    }
    private async Task ReleaseAutomaticLeaseBestEffortAsync(
        PortLeaseRequest request,
        PortLease lease)
    {
        if (lease.NodeId != request.NodeId || lease.ServiceId != request.ServiceId)
        {
            return;
        }

        try
        {
            await _leaseStore.ApplyAsync(
                PortLeaseIntent.ReleaseLease(
                    new PortLeaseRelease(request.NodeId, request.ServiceId, lease.Port, lease.Version)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }
    private bool IsStopping => Volatile.Read(ref _stopping) != 0;

    private static bool IsCurrentGeneration(ServiceSlot slot, ServiceGeneration generation)
    {
        lock (slot.Gate)
        {
            return ReferenceEquals(slot.Active, generation);
        }
    }

    private async Task HandleProcessExitAsync(
        Guid serviceId,
        ProcessInstanceId? instanceId,
        bool successfulExit,
        DateTimeOffset exitedAt)
    {
        if (IsStopping || !_slots.TryGetValue(serviceId, out var slot))
        {
            return;
        }

        ServiceGeneration? generation;
        lock (slot.Gate)
        {
            generation = slot.Active;
            if (generation is null ||
                (instanceId is { } observed && generation.Supervisor.ActiveProcessInstance != observed))
            {
                return;
            }

            generation.Ready = false;
        }
        PublishServiceState(
            generation.Configuration.Id,
            generation.SnapshotVersion,
            "unavailable");

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        if (IsStopping)
        {
            return;
        }

        SupervisorOperationResult result;
        lock (slot.Gate)
        {
            if (IsStopping || !ReferenceEquals(slot.Active, generation) ||
                (instanceId is { } observed && generation.Supervisor.ActiveProcessInstance != observed))
            {
                return;
            }

            result = generation.Supervisor.RecordProcessExit(successfulExit, exitedAt);
        }

        if (result.Restart is not { ShouldRestart: true, NotBefore: { } notBefore })
        {
            await StopGenerationAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "stopped");
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Active, generation))
                {
                    slot.Active = null;
                }
            }

            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            return;
        }

        if (!IsStopping)
        {
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "restarting");
            await RestartAfterAsync(slot, generation, notBefore, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ObserveReadyHealthAsync(CancellationToken cancellationToken)
    {
        foreach (var slot in _slots.Values)
        {
            if (IsStopping || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ServiceGeneration? generation;
            lock (slot.Gate) generation = slot.Active;
            if (generation is null || !generation.Ready)
            {
                continue;
            }

            SupervisorOperationResult result;
            try
            {
                result = await generation.Supervisor.ObserveHealthAsync(
                    generation.HealthRetryState,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                continue;
            }

            var decision = result.Health;
            if (decision is null)
            {
                continue;
            }

            var withdraw = false;
            var terminal = false;
            lock (slot.Gate)
            {
                if (IsStopping || !ReferenceEquals(slot.Active, generation) || !generation.Ready)
                {
                    continue;
                }

                generation.HealthRetryState = decision.NextState;
                if (decision.Action == HealthRetryAction.Healthy)
                {
                    if (result.Lease is { } lease && !lease.IsExpired(DateTimeOffset.UtcNow))
                    {
                        generation.Lease = lease;
                        generation.Ready = true;
                    }
                    else
                    {
                        generation.Ready = false;
                        withdraw = true;
                    }
                }
                else if (decision.Action is HealthRetryAction.Failed or HealthRetryAction.TimedOut)
                {
                    terminal = true;
                }
            }

            if (withdraw)
            {
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "unavailable");
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
            }

            if (terminal)
            {
                await HandleTerminalHealthAsync(slot, generation).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleTerminalHealthAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        if (IsStopping)
        {
            return;
        }

        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Active, generation))
            {
                return;
            }

            generation.Ready = false;
        }
        PublishServiceState(
            generation.Configuration.Id,
            generation.SnapshotVersion,
            "unavailable");

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        if (IsStopping)
        {
            return;
        }

        await StopGenerationAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
        if (IsStopping)
        {
            return;
        }

        SupervisorOperationResult result;
        lock (slot.Gate)
        {
            if (IsStopping || !ReferenceEquals(slot.Active, generation))
            {
                return;
            }

            result = generation.Supervisor.RecordProcessExit(false, DateTimeOffset.UtcNow);
            if (result.Restart is not { ShouldRestart: true })
            {
                slot.Active = null;
            }
        }

        if (result.Restart is { ShouldRestart: true, NotBefore: { } notBefore } && !IsStopping)
        {
            _ = RestartAfterAsync(slot, generation, notBefore, CancellationToken.None);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "restarting");
        }
        else
        {
            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "stopped");
        }
    }


    private async Task RestartAfterAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        if (IsStopping)
        {
            return;
        }

        var delay = notBefore - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (IsStopping)
        {
            return;
        }

        var snapshot = _snapshotHolder.Current;
        if (snapshot is null || !snapshot.Services.Any(value =>
                value.Id == generation.Configuration.Id &&
                value.Version == generation.Configuration.Version &&
                value.Enabled))
        {
            return;
        }
        if (!_runtimeState.NewServicesAllowed)
        {
            return;
        }


        Task<SupervisorOperationResult>? startTask = null;
        lock (_lifecycleGate)
        {
            if (!IsStopping)
            {
                startTask = generation.Supervisor.StartAsync(
                    DateTimeOffset.UtcNow,
                    _shutdownCts.Token).AsTask();
            }
        }

        if (startTask is null)
        {
            return;
        }

        var started = await startTask.ConfigureAwait(false);
        if (IsStopping)
        {
            await StopGenerationAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (started.Status != SupervisorOperationStatus.Applied || generation.Supervisor.Lease is null)
        {
            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            return;
        }

        var retry = HealthRetryState.Start(
            generation.Configuration.Id,
            DateTimeOffset.UtcNow,
            HealthPolicy.StartupTimeout);
        while (!IsStopping)
        {
            var now = DateTimeOffset.UtcNow;
            var health = await generation.Supervisor.ObserveHealthAsync(retry, now, cancellationToken).ConfigureAwait(false);
            var decision = health.Health;
            if (decision is null)
            {
                await StopGenerationAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            retry = decision.NextState;
            if (decision.Action == HealthRetryAction.Healthy && generation.Supervisor.Lease is { } lease && !lease.IsExpired(now))
            {
                generation.HealthRetryState = retry;
                generation.Lease = lease;
                generation.Ready = true;
                if (!IsStopping)
                {
                    await PublishReadyEndpointsAsync().ConfigureAwait(false);
                    PublishServiceState(
                        generation.Configuration.Id,
                        generation.SnapshotVersion,
                        "ready");
                }

                return;
            }

            if (decision.Action is HealthRetryAction.Cancelled or HealthRetryAction.Failed or HealthRetryAction.TimedOut)
            {
                await StopGenerationAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
                if (!IsStopping)
                {
                    await PublishReadyEndpointsAsync().ConfigureAwait(false);
                }

                return;
            }

            if (decision.NextAttemptAt is { } next)
            {
                var wait = next - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WithdrawAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        if (!_slots.TryGetValue(serviceId, out var slot))
        {
            return;
        }

        ServiceGeneration? generation;
        lock (slot.Gate)
        {
            generation = slot.Active;
            slot.Active = null;
            slot.Startup = null;
        }

        if (generation is not null)
        {
            await StopGenerationAsync(slot, generation, cancellationToken).ConfigureAwait(false);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "stopped");
        }

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
    }

    private static async Task StopGenerationAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        CancellationToken cancellationToken)
    {
        generation.Ready = false;
        try
        {
            await generation.Supervisor.StopAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task PublishReadyEndpointsAsync()
    {
        await _publicationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsStopping)
            {
                _endpointPublisher.Publish(Array.Empty<HostServiceEndpointLease>());
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var leases = new List<HostServiceEndpointLease>();
            foreach (var slot in _slots.Values)
            {
                ServiceGeneration? generation;
                lock (slot.Gate) generation = slot.Active;
                if (generation is { Ready: true, Lease: { } lease } && !lease.IsExpired(now))
                {
                    leases.Add(new HostServiceEndpointLease(lease.ServiceId, lease.Port, lease.ExpiresAt));
                }
            }

            _endpointPublisher.Publish(leases);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private sealed class ServiceSlot
    {
        internal readonly object Gate = new();
        internal ServiceGeneration? Active;
        internal Task<HostServiceReadinessResult>? Startup;
        internal long StartupGeneration;
    }

    private sealed class ServiceGeneration
    {
        internal ServiceGeneration(
            ServiceConfiguration configuration,
            ServiceSupervisor supervisor,
            PortLease lease,
            long snapshotVersion,
            HealthRetryState healthRetryState)
        {
            Configuration = configuration;
            Supervisor = supervisor;
            Lease = lease;
            SnapshotVersion = snapshotVersion;
            HealthRetryState = healthRetryState;
            Ready = true;
        }

        internal ServiceConfiguration Configuration { get; }
        internal ServiceSupervisor Supervisor { get; }
        internal PortLease? Lease { get; set; }
        internal long SnapshotVersion { get; }
        internal HealthRetryState HealthRetryState { get; set; }
        internal bool Ready { get; set; }
    }
}
