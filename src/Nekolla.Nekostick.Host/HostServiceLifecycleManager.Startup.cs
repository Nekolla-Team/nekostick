using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;
using ContractHealthKind = Nekolla.Nekostick.Contracts.ServiceHealthCheckType;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using ContractStartMode = Nekolla.Nekostick.Contracts.ServiceStartMode;
using DomainHealthKind = Nekolla.Nekostick.Domain.ServiceHealthCheckKind;
using DomainRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostServiceLifecycleManager
{
    /// <summary>Ignores process-exit notifications that do not identify a process generation.</summary>
    public void NotifyProcessExit(Guid serviceId, bool successfulExit)
    {
    }

    /// <summary>Completes a legacy service-only process-exit handoff without mutating lifecycle state.</summary>
    internal Task NotifyProcessExitAsync(Guid serviceId, bool successfulExit)
    {
        _ = this;
        return Task.CompletedTask;
    }

    /// <summary>Records a process-exit observation for the identified process generation.</summary>
    public void NotifyProcessExit(Guid serviceId, ProcessInstanceId instanceId, bool successfulExit) =>
        _ = NotifyProcessExitAsync(serviceId, instanceId, successfulExit);

    /// <summary>Completes the identity-aware process-exit handoff.</summary>
    internal Task NotifyProcessExitAsync(Guid serviceId, ProcessInstanceId instanceId, bool successfulExit) =>
        HandleProcessExitAsync(serviceId, instanceId, successfulExit, DateTimeOffset.UtcNow);

    private void HandleProcessExitObservation(ProcessExitObservation observation) =>
        _ = HandleProcessExitAsync(
            observation.ServiceId,
            observation.InstanceId,
            observation.SuccessfulExit,
            observation.ExitedAt);
    private async Task<HostServiceReadinessResult> StartOrSwitchAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service)
    {
        try
        {
            if (IsStopping)
            {
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            if (!_runtimeState.NewServicesAllowed)
            {
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.DatabaseUnavailable);
            }

            var candidate = await StartGenerationAsync(snapshot, service, _shutdownCts.Token).ConfigureAwait(false);
            if (candidate is null)
            {
                if (!IsStopping)
                {
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                }

                return IsStopping
                    ? new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled)
                    : new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
            }

            ServiceGeneration? old = null;
            var accepted = false;
            lock (_lifecycleGate)
            {
                if (!IsStopping)
                {
                    lock (slot.Gate)
                    {
                        old = slot.Active;
                        slot.Active = candidate;
                    }

                    accepted = true;
                }
            }

            if (!accepted)
            {
                await StopGenerationAsync(slot, candidate, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(service.Id, snapshot.Version, "stopped");
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            PublishServiceState(service.Id, candidate.SnapshotVersion, "ready");
            if (old is not null && !ReferenceEquals(old, candidate))
            {
                await StopGenerationAsync(slot, old, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(old.Configuration.Id, old.SnapshotVersion, "stopped");
            }

            if (IsStopping)
            {
                await StopGenerationAsync(slot, candidate, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(service.Id, candidate.SnapshotVersion, "stopped");
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Ready, candidate.Supervisor.Snapshot);
        }
        catch (OperationCanceledException)
        {
            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }
        catch
        {
            PublishServiceState(service.Id, snapshot.Version, "unavailable");
            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
        }
        finally
        {
            lock (slot.Gate)
            {
                slot.Startup = null;
            }
        }
    }

    private async Task<ServiceGeneration?> StartGenerationAsync(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        CancellationToken cancellationToken)
    {
        var rangeStart = snapshot.GlobalSettings.AutoPortRangeStart;
        var rangeEnd = snapshot.GlobalSettings.AutoPortRangeEnd;
        if (rangeStart < 1 || rangeEnd > 65535 || rangeStart > rangeEnd)
        {
            return null;
        }

        var request = PortLeaseRequest.Automatic(
            _nodeId,
            service.Id,
            LeasePolicy.TimeToLive,
            rangeStart,
            rangeEnd);
        var now = DateTimeOffset.UtcNow;
        PortLeaseOperationResult leaseResult;
        try
        {
            leaseResult = await _leaseStore.ApplyAsync(
                PortLeaseIntent.Acquire(request),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            _runtimeState.MarkDatabaseUnavailable();
            return null;
        }

        var acquired = leaseResult.Lease;
        if (leaseResult.Status != PortLeaseOperationStatus.Applied || acquired is null)
        {
            if (leaseResult.Status == PortLeaseOperationStatus.DatabaseUnavailable)
            {
                _runtimeState.MarkDatabaseUnavailable();
            }

            return null;
        }

        if (IsStopping ||
            acquired.NodeId != request.NodeId ||
            acquired.ServiceId != request.ServiceId ||
            acquired.Port < rangeStart ||
            acquired.Port > rangeEnd ||
            acquired.IsExpired(now))
        {
            await ReleaseAutomaticLeaseBestEffortAsync(request, acquired).ConfigureAwait(false);
            return null;
        }

        ServiceSupervisor? supervisor = null;
        Task<SupervisorOperationResult>? startTask = null;
        lock (_lifecycleGate)
        {
            if (!IsStopping)
            {
                try
                {
                    supervisor = CreateSupervisor(service, acquired.Port, acquired, now);
                    startTask = supervisor.StartAsync(now, _shutdownCts.Token).AsTask();
                }
                catch
                {
                    startTask = null;
                }
            }
        }

        if (supervisor is null)
        {
            await ReleaseAutomaticLeaseBestEffortAsync(request, acquired).ConfigureAwait(false);
            return null;
        }

        if (startTask is null)
        {
            await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        SupervisorOperationResult started;
        try
        {
            started = await startTask.ConfigureAwait(false);
        }
        catch
        {
            await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (started.Status != SupervisorOperationStatus.Applied || supervisor.Lease is null)
        {
            if (started.Reason == ServiceStateReasonCode.DatabaseUnavailable)
            {
                _runtimeState.MarkDatabaseUnavailable();
            }

            return null;
        }

        if (IsStopping)
        {
            await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        var retry = HealthRetryState.Start(service.Id, DateTimeOffset.UtcNow, HealthPolicy.StartupTimeout);
        while (true)
        {
            var observationAt = DateTimeOffset.UtcNow;
            var health = await supervisor.ObserveHealthAsync(retry, observationAt, cancellationToken).ConfigureAwait(false);
            var decision = health.Health;
            if (decision?.Action == HealthRetryAction.Healthy && supervisor.Lease is { } readyLease && !readyLease.IsExpired(observationAt))
            {
                if (IsStopping)
                {
                    await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                return new ServiceGeneration(service, supervisor, readyLease, snapshot.Version, decision.NextState);
            }

            if (decision is null || decision.Action is HealthRetryAction.Cancelled or HealthRetryAction.Failed or HealthRetryAction.TimedOut)
            {
                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            retry = decision.NextState;
            if (decision.NextAttemptAt is { } next)
            {
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }


    private void PublishServiceState(Guid serviceId, long version, string state)
    {
        HostCoreEventPublisher.Publish(
            _runtimeManager,
            ExtensionCoreEventKind.ServiceStateChanged,
            new
            {
                serviceId,
                version,
                state
            });
    }
}
