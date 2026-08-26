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
            HostLogMessages.ServiceRestartScheduled(_logger, generation.Configuration.Id);
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
                    HostLogMessages.ServiceReady(_logger, generation.Configuration.Id, generation.SnapshotVersion);
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
}
