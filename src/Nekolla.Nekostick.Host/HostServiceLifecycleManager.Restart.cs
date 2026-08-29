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
            if (!ReferenceEquals(slot.Active, generation) ||
                !_retiringGenerations.TryAdd(generation, new RetiringGenerationState()))
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
            if (IsStopping || !ReferenceEquals(slot.Active, generation))
            {
                return;
            }

            result = generation.Supervisor.RecordProcessExitPreservingInstance(false, DateTimeOffset.UtcNow);
        }

        if (result.Restart is { ShouldRestart: true, NotBefore: { } notBefore } && !IsStopping)
        {
            HostLogMessages.ServiceRestartScheduled(_logger, generation.Configuration.Id);
            _ = RestartTerminalAfterAsync(slot, generation, notBefore, CancellationToken.None);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "restarting");
            return;
        }

        _ = StopRetiringGenerationAsync(slot, generation);
    }

    private async Task RestartAfterAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            await RestartAfterCrashCoreAsync(slot, generation, notBefore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(RestartAfterAsync));
        }
    }

    private async Task RestartAfterCrashCoreAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        var delay = notBefore - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (IsStopping || !IsCurrentGeneration(slot, generation))
        {
            return;
        }

        if (!_runtimeState.NewServicesAllowed)
        {
            // Preserve the pre-existing generation and lease until normal renewal/expiry can recover.
            return;
        }

        var snapshot = _snapshotHolder.Current;
        if (snapshot is null || !snapshot.Services.Any(value =>
                value.Id == generation.Configuration.Id &&
                value.Version == generation.Configuration.Version &&
                value.Enabled) ||
            !IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id))
        {
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        Task<HostServiceReadinessResult>? existingStartup = null;
        Task<SupervisorOperationResult>? startTask = null;
        lock (_lifecycleGate)
        {
            if (IsStopping)
            {
                return;
            }

            lock (slot.Gate)
            {
                if (IsStopping || !ReferenceEquals(slot.Active, generation))
                {
                    return;
                }

                if (slot.Startup is { } startup)
                {
                    existingStartup = startup;
                }
                else
                {
                    // Crash recovery intentionally reuses the held lease and supervisor instance.
                    startTask = generation.Supervisor.StartAsync(
                        DateTimeOffset.UtcNow,
                        _shutdownCts.Token).AsTask();
                }
            }
        }

        if (existingStartup is not null)
        {
            try
            {
                await existingStartup.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(RestartAfterAsync));
            }

            if (IsStopping ||
                !IsCurrentGeneration(slot, generation) ||
                generation.Supervisor.ActiveProcessInstance is not null)
            {
                return;
            }

            lock (_lifecycleGate)
            {
                if (IsStopping)
                {
                    return;
                }

                lock (slot.Gate)
                {
                    if (IsStopping ||
                        !ReferenceEquals(slot.Active, generation) ||
                        generation.Supervisor.ActiveProcessInstance is not null)
                    {
                        return;
                    }

                    // The request-triggered startup failed without replacing the dead generation.
                    startTask = generation.Supervisor.StartAsync(
                        DateTimeOffset.UtcNow,
                        _shutdownCts.Token).AsTask();
                }
            }
        }

        if (startTask is null)
        {
            return;
        }

        var started = await startTask.ConfigureAwait(false);
        if (started.Status != SupervisorOperationStatus.Applied)
        {
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        generation.ProcessExitRecorded = false;

        if (IsStopping)
        {
            return;
        }

        var retry = HealthRetryState.Start(
            generation.Configuration.Id,
            DateTimeOffset.UtcNow,
            HealthPolicy.StartupTimeout);
        while (true)
        {
            var observationAt = DateTimeOffset.UtcNow;
            var health = await generation.Supervisor.ObserveHealthAsync(
                retry,
                observationAt,
                cancellationToken).ConfigureAwait(false);
            var decision = health.Health;
            if (decision?.Action == HealthRetryAction.Healthy &&
                generation.Supervisor.Lease is { } readyLease &&
                !readyLease.IsExpired(observationAt))
            {
                lock (slot.Gate)
                {
                    if (IsStopping || !ReferenceEquals(slot.Active, generation))
                    {
                        return;
                    }

                    generation.Lease = readyLease;
                    generation.Ready = true;
                }

                await PublishReadyEndpointsAsync().ConfigureAwait(false);
                HostLogMessages.ServiceReady(_logger, generation.Configuration.Id, generation.SnapshotVersion);
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "ready");
                return;
            }

            if (decision is null || decision.Action is HealthRetryAction.Cancelled or HealthRetryAction.Failed or HealthRetryAction.TimedOut)
            {
                await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
                var removed = false;
                lock (slot.Gate)
                {
                    if (ReferenceEquals(slot.Active, generation))
                    {
                        slot.Active = null;
                        removed = true;
                    }
                }

                if (removed)
                {
                    await PublishReadyEndpointsAsync().ConfigureAwait(false);
                    PublishServiceState(
                        generation.Configuration.Id,
                        generation.SnapshotVersion,
                        "stopped");
                }

                return;
            }

            retry = decision.NextState;
            if (decision.NextAttemptAt is { } next)
            {
                var retryDelay = next - DateTimeOffset.UtcNow;
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task RestartTerminalAfterAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            await RestartTerminalAfterCoreAsync(slot, generation, notBefore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoveRetiringGeneration(generation);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(RestartTerminalAfterAsync));
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
        }
    }

    private async Task RestartTerminalAfterCoreAsync(
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
            RemoveRetiringGeneration(generation);
            return;
        }

        var snapshot = _snapshotHolder.Current;
        if (snapshot is null || !snapshot.Services.Any(value =>
                value.Id == generation.Configuration.Id &&
                value.Version == generation.Configuration.Version &&
                value.Enabled) ||
            !IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id))
        {
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
            return;
        }
        if (!_runtimeState.NewServicesAllowed)
        {
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
            return;
        }

        // A request-triggered startup may already own slot.Startup. Share it rather than starting a second candidate.
        // If that startup fails without changing slot.Active, make one backoff-owned attempt below.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Task<HostServiceReadinessResult> startup;
            var ownsStartup = false;
            lock (_lifecycleGate)
            {
                if (IsStopping)
                {
                    RemoveRetiringGeneration(generation);
                    return;
                }

                lock (slot.Gate)
                {
                    if (IsStopping || !ReferenceEquals(slot.Active, generation))
                    {
                        RemoveRetiringGeneration(generation);
                        return;
                    }

                    if (slot.Startup is { } existingStartup)
                    {
                        startup = existingStartup;
                    }
                    else
                    {
                        ownsStartup = true;
                        startup = StartOrSwitchAsync(
                            slot,
                            snapshot,
                            generation.Configuration,
                            stopReplacedGeneration: false);
                    }
                }
            }

            try
            {
                await startup.ConfigureAwait(false);
            }
            catch
            {
                await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
                return;
            }

            if (IsStopping)
            {
                RemoveRetiringGeneration(generation);
                return;
            }

            ServiceGeneration? active;
            lock (slot.Gate)
            {
                active = slot.Active;
            }

            if (ReferenceEquals(active, generation))
            {
                if (ownsStartup || attempt == 1)
                {
                    await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            // A shared request startup already performed the replacement and its normal path owns old-generation cleanup.
            if (!ownsStartup || active is null)
            {
                RemoveRetiringGeneration(generation);
                return;
            }

            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
            return;
        }
    }

    private async Task StopRetiringGenerationAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        try
        {
            await StopRetiringGenerationCoreAsync(slot, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RemoveRetiringGeneration(generation);
        }
        catch (Exception exception)
        {
            RemoveRetiringGeneration(generation);
            HostLogMessages.FailureDetails(_logger, exception, nameof(StopRetiringGenerationAsync));
        }
    }

    private async Task StopRetiringGenerationCoreAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        try
        {
            await DrainAndStopGenerationAsync(slot, generation).ConfigureAwait(false);
        }
        finally
        {
            RemoveRetiringGeneration(generation);
        }

        if (IsStopping)
        {
            return;
        }

        var removed = false;
        lock (slot.Gate)
        {
            if (ReferenceEquals(slot.Active, generation))
            {
                slot.Active = null;
                removed = true;
            }
        }

        if (removed)
        {
            await PublishReadyEndpointsAsync().ConfigureAwait(false);
        }

        PublishServiceState(
            generation.Configuration.Id,
            generation.SnapshotVersion,
            "stopped");
    }

    private async Task StopOrReleaseGenerationAfterExitAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        CancellationToken cancellationToken)
    {
        if (generation.ProcessExitRecorded && generation.Supervisor.ActiveProcessInstance is null)
        {
            await generation.Supervisor.AcknowledgeProcessExitAsync().ConfigureAwait(false);
            generation.Lease = null;
            return;
        }

        await StopGenerationAsync(slot, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAndStopGenerationAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        if (_retiringGenerations.TryGetValue(generation, out var retiring) &&
            !TryClaimRetiringStop(retiring))
        {
            if (Volatile.Read(ref retiring.Outcome) == RetiringGenerationState.ProcessExited)
            {
                await retiring.ProcessExitAcknowledged.Task.ConfigureAwait(false);
            }

            return;
        }

        if (generation.ProcessExitRecorded && generation.Supervisor.ActiveProcessInstance is null)
        {
            await generation.Supervisor.AcknowledgeProcessExitAsync().ConfigureAwait(false);
            generation.Lease = null;
            return;
        }

        if (generation.Lease is { } lease)
        {
            await _drainTracker.WaitDrainedAsync(
                generation.Configuration.Id,
                lease.Port,
                StopGracePeriod,
                CancellationToken.None).ConfigureAwait(false);
        }

        await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
    }


    private void RemoveRetiringGeneration(ServiceGeneration generation) =>
        _retiringGenerations.TryRemove(generation, out _);

    private static bool TryClaimRetiringProcessExit(RetiringGenerationState state) =>
        Interlocked.CompareExchange(
            ref state.Outcome,
            RetiringGenerationState.ProcessExited,
            RetiringGenerationState.Pending) == RetiringGenerationState.Pending;

    private static bool TryClaimRetiringStop(RetiringGenerationState state) =>
        Interlocked.CompareExchange(
            ref state.Outcome,
            RetiringGenerationState.StopClaimed,
            RetiringGenerationState.Pending) == RetiringGenerationState.Pending;

    private sealed class RetiringGenerationState
    {
        internal const int Pending = 0;
        internal const int ProcessExited = 1;
        internal const int StopClaimed = 2;

        internal int Outcome;
        internal TaskCompletionSource<bool> ProcessExitAcknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
