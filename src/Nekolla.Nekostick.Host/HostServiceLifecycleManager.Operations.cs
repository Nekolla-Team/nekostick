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
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(ReleaseAutomaticLeaseBestEffortAsync));
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
        ProcessInstanceId instanceId,
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
            if (generation is null || generation.Supervisor.ActiveProcessInstance != instanceId)
            {
                return;
            }

            generation.Ready = false;
        }
        if (successfulExit)
        {
            HostLogMessages.ServiceExitedSuccessfully(_logger, serviceId);
        }
        else
        {
            HostLogMessages.ServiceExitedUnexpectedly(_logger, serviceId);
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
                generation.Supervisor.ActiveProcessInstance != instanceId)
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
            HostLogMessages.ServiceRestartScheduled(_logger, serviceId);
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
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(ObserveReadyHealthAsync));
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

    private async Task StopGenerationAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        CancellationToken cancellationToken)
    {
        generation.Ready = false;
        HostLogMessages.ServiceStopped(_logger, generation.Configuration.Id);
        try
        {
            await generation.Supervisor.StopAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(StopGenerationAsync));
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
            var serviceOwners = _snapshotHolder.RoutingSnapshot?.ServiceOwners;
            var leases = new List<HostServiceEndpointLease>();
            foreach (var slot in _slots.Values)
            {
                ServiceGeneration? generation;
                lock (slot.Gate) generation = slot.Active;
                if (generation is not { Ready: true, Lease: { } lease } || lease.IsExpired(now) ||
                    serviceOwners is null || !serviceOwners.TryGetValue(lease.ServiceId, out var owner))
                {
                    continue;
                }

                leases.Add(new HostServiceEndpointLease(lease.ServiceId, lease.Port, lease.ExpiresAt, owner));
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
            HealthRetryState healthRetryState,
            string? ownerExtensionId)
        {
            Configuration = configuration;
            Supervisor = supervisor;
            Lease = lease;
            SnapshotVersion = snapshotVersion;
            HealthRetryState = healthRetryState;
            OwnerExtensionId = ownerExtensionId;
            Ready = true;
        }

        internal ServiceConfiguration Configuration { get; }
        internal ServiceSupervisor Supervisor { get; }
        internal PortLease? Lease { get; set; }
        internal long SnapshotVersion { get; }
        internal HealthRetryState HealthRetryState { get; set; }
        internal string? OwnerExtensionId { get; }
        internal bool Ready { get; set; }

    }
}
