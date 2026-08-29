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
        RetiringGenerationState? retiring;
        lock (slot.Gate)
        {
            generation = slot.Active;
            if (generation is null || generation.Supervisor.ActiveProcessInstance != instanceId)
            {
                return;
            }

            _retiringGenerations.TryGetValue(generation, out retiring);
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

        if (retiring is not null)
        {
            if (TryClaimRetiringProcessExit(retiring))
            {
                try
                {
                    await generation.Supervisor.AcknowledgeProcessExitAsync().ConfigureAwait(false);
                    generation.Lease = null;
                }
                finally
                {
                    retiring.ProcessExitAcknowledged.TrySetResult(true);
                }
            }

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
            generation.ProcessExitRecorded = true;
        }

        if (result.Restart is not { ShouldRestart: true, NotBefore: { } notBefore })
        {
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
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
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task PublishVerifiedEndpointsAsync(
        IReadOnlyList<HostServiceEndpointLease> dbLeases)
    {
        ArgumentNullException.ThrowIfNull(dbLeases);
        await _publicationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsStopping)
            {
                _endpointPublisher.Publish(Array.Empty<HostServiceEndpointLease>());
                return;
            }

            // The fresh-identity/stale-DB-read asymmetry is intentional; the unconditional 1s authoritative republish bounds omissions.
            var readyIdentities = GetActiveReadyLeases(DateTimeOffset.UtcNow)
                .Select(static lease => (lease.ServiceId, lease.Port))
                .ToHashSet();
            var verifiedLeases = new List<HostServiceEndpointLease>(dbLeases.Count);
            foreach (var lease in dbLeases)
            {
                if (lease is not null && readyIdentities.Contains((lease.ServiceId, lease.Port)))
                {
                    verifiedLeases.Add(lease);
                }
            }

            _endpointPublisher.Publish(verifiedLeases);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private ImmutableArray<PortLease> GetActiveReadyLeases(DateTimeOffset now)
    {
        var leases = ImmutableArray.CreateBuilder<PortLease>();
        foreach (var slot in _slots.Values)
        {
            lock (slot.Gate)
            {
                if (slot.Active is not { Ready: true, Lease: { } lease } || lease.IsExpired(now))
                {
                    continue;
                }

                leases.Add(lease);
            }
        }

        return leases.ToImmutable();
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
            foreach (var lease in GetActiveReadyLeases(now))
            {
                if (serviceOwners is null || !serviceOwners.TryGetValue(lease.ServiceId, out var owner))
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
        private volatile PortLease? _lease;
        private volatile bool _ready;
        private volatile bool _processExitRecorded;

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
            _lease = lease;
            SnapshotVersion = snapshotVersion;
            HealthRetryState = healthRetryState;
            OwnerExtensionId = ownerExtensionId;
            _ready = true;
        }

        internal ServiceConfiguration Configuration { get; }
        internal ServiceSupervisor Supervisor { get; }
        internal PortLease? Lease
        {
            get => _lease;
            set => _lease = value;
        }
        internal long SnapshotVersion { get; }
        internal HealthRetryState HealthRetryState { get; set; }
        internal string? OwnerExtensionId { get; }
        internal bool Ready
        {
            get => _ready;
            set => _ready = value;
        }
        internal bool ProcessExitRecorded
        {
            get => _processExitRecorded;
            set => _processExitRecorded = value;
        }

    }
}
