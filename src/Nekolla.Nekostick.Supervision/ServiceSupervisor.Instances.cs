using System.Threading;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

public sealed partial class ServiceSupervisor
{
    /// <summary>Gets the current operating-system process ID when safely known to be running.</summary>
    public int? ActiveProcessId => TryGetActiveProcessTelemetry(out var processId, out _) ? processId : null;

    /// <summary>Gets the executor-established process start instant when safely known to be running.</summary>
    public DateTimeOffset? ActiveProcessStartedAt => TryGetActiveProcessTelemetry(out _, out var startedAt) ? startedAt : null;

    /// <summary>Gets the generation token used to match a pending exit observation.</summary>
    public ProcessInstanceId? ActiveProcessInstance
    {
        get
        {
            lock (processHolderGate)
            {
                if (processInstance is { } active && processObservation is { } observation && active.Id != observation.Id)
                {
                    return null;
                }

                return processObservation?.Id;
            }
        }
    }

    /// <summary>Reads one process telemetry tuple without mixing generations during a loss race.</summary>
    public bool TryGetActiveProcessTelemetry(out int? processId, out DateTimeOffset? startedAt) =>
        TryGetActiveProcessTelemetry(out _, out processId, out startedAt);

    /// <summary>Reads process telemetry together with the generation that established it.</summary>
    public bool TryGetActiveProcessTelemetry(
        out ProcessInstanceId? instanceId,
        out int? processId,
        out DateTimeOffset? startedAt)
    {
        var active = ReadTrustedProcess();
        if (active is null)
        {
            instanceId = null;
            processId = null;
            startedAt = null;
            return false;
        }

        instanceId = active.Id;
        processId = active.ProcessId;
        startedAt = active.StartedAt;
        return true;
    }

    private readonly object processHolderGate = new();
    private ProcessInstanceHolder? processObservation;

    private sealed record ProcessInstanceHolder(
        ProcessInstanceId Id,
        int? ProcessId,
        DateTimeOffset? StartedAt);

    private void RememberProcessInstance(ProcessOperationResult result, bool accepted)
    {
        lock (processHolderGate)
        {
            processInstance = null;
            processObservation = null;
            if (!accepted || result.InstanceId is not { } instanceId)
            {
                return;
            }

            var holder = new ProcessInstanceHolder(instanceId, result.ProcessId, NormalizeStartedAt(result.StartedAt));
            processInstance = holder;
            processObservation = holder;
        }
    }

    private ProcessInstanceHolder? ReadTrustedProcess()
    {
        lock (processHolderGate)
        {
            return ReadTrustedProcessLocked();
        }
    }

    private ProcessInstanceHolder? ReadTrustedProcessLocked()
    {
        var active = processInstance;
        if (active is null)
        {
            return null;
        }

        if (processObservation is not { } observation || observation.Id != active.Id)
        {
            processInstance = null;
            processObservation = null;
            return null;
        }

        var running = processExecutor is IProcessLiveness liveness && IsRunningSafely(liveness, active);
        if (running)
        {
            return active;
        }

        processInstance = null;
        processObservation = active;
        return null;
    }

    private bool IsRunningSafely(IProcessLiveness liveness, ProcessInstanceHolder active)
    {
        try
        {
            return liveness.IsRunning(launchSpecification.ServiceId, active.Id);
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessLivenessFailed(
                _logger,
                exception,
                launchSpecification.ServiceId,
                active.Id.ToString());
            return false;
        }
    }

    private DateTimeOffset? NormalizeStartedAt(DateTimeOffset? startedAt)
    {
        if (startedAt is not { } value)
        {
            return null;
        }

        try
        {
            var utc = value.ToUniversalTime();
            var now = DateTimeOffset.UtcNow;
            return utc > now ? now : utc;
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessTimestampNormalizationFailed(
                _logger,
                exception,
                launchSpecification.ServiceId);
            return null;
        }
    }

    private async ValueTask<ProcessOperationResult> StopProcessAsync(CancellationToken cancellationToken)
    {
        ProcessInstanceHolder? active;
        bool hasTrackedIdentity;
        lock (processHolderGate)
        {
            hasTrackedIdentity = processInstance is not null || processObservation is not null;
            active = ReadTrustedProcessLocked();
            hasTrackedIdentity |= processInstance is not null || processObservation is not null;
        }

        ProcessOperationResult result;
        if (active is not null)
        {
            if (processExecutor is not IProcessInstanceExecutor instanceExecutor)
            {
                return new ProcessOperationResult(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StopRequested);
            }

            result = await instanceExecutor.StopAsync(active.Id, stopGracePeriod, cancellationToken).ConfigureAwait(false);
        }
        else if (hasTrackedIdentity)
        {
            ClearProcessInstance();
            return new ProcessOperationResult(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted);
        }
        else
        {
            result = await processExecutor.StopAsync(
                launchSpecification.ServiceId,
                stopGracePeriod,
                cancellationToken).ConfigureAwait(false);
        }

        if (result.Status is ProcessOperationStatus.Accepted or ProcessOperationStatus.Completed)
        {
            ClearProcessInstance();
        }

        return result;
    }

    private void ClearProcessInstance()
    {
        lock (processHolderGate)
        {
            processInstance = null;
            processObservation = null;
        }
    }
    /// <summary>Records an external process exit and produces a pure bounded restart plan.</summary>
    /// <param name="successfulExit">Whether the process exited successfully.</param>
    /// <param name="now">The exit timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result containing a restart plan.</returns>
    public SupervisorOperationResult RecordProcessExit(
        bool successfulExit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ClearProcessInstance();
        return RecordProcessExitCore(successfulExit, now, cancellationToken);
    }

    /// <summary>Records a terminal-health restart decision while retaining the active process identity for a later instance-targeted stop.</summary>
    /// <param name="successfulExit">Whether the process exited successfully.</param>
    /// <param name="now">The decision timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result containing a restart plan.</returns>
    public SupervisorOperationResult RecordProcessExitPreservingInstance(
        bool successfulExit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        RecordProcessExitCore(successfulExit, now, cancellationToken);
    /// <summary>Clears a process identity and records whether its lease release completed.</summary>
    /// <returns>The outcome of the serialized lease release.</returns>
    public async ValueTask<SupervisorOperationResult> AcknowledgeProcessExitAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ClearProcessInstance();
            return await ReleaseLeaseCoreAsync(
                CancellationToken.None,
                processStopped: true).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private SupervisorOperationResult RecordProcessExitCore(
        bool successfulExit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Synchronous gate acquisition: the critical section below awaits nothing, and no
        // lifecycleGate holder calls back into this method, so the brief blocking wait cannot
        // deadlock. Gating keeps the epoch increment and snapshot exchange atomic against
        // ApplyHealthObservationAsync's check-then-commit.
        lifecycleGate.Wait(CancellationToken.None);
        try
        {
            Interlocked.Increment(ref lifecycleEpoch);
            var current = Snapshot;
            var next = Exchange(ServiceStateTransition.RecordProcessExit(current, successfulExit, now));
            var policy = next.Desired == DesiredServiceState.Running
                ? restartPolicy
                : ServiceRestartPolicy.Never;
            var plan = RestartPlanner.Plan(policy, successfulExit, next.RestartAttempts, now, restartBackoff, restartJitter, cancellationToken);
            var planned = Exchange(ServiceStateTransition.RecordRestartPlan(next, plan, now));
            return Result(plan.ShouldRestart ? SupervisorOperationStatus.Applied : SupervisorOperationStatus.Rejected, plan.Reason, planned, Lease, plan);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask ReleaseInitialLeaseAfterCancelledGateWaitAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!initialLeasePending)
            {
                return;
            }

            var initialLease = Volatile.Read(ref lease);
            initialLeasePending = false;
            var release = await ReleaseLeaseCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (release.Status == SupervisorOperationStatus.Applied && initialLease is not null)
            {
                SupervisionLogMessages.InitialLeaseReleased(_logger, initialLease.ServiceId, initialLease.Port);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask<SupervisorOperationResult?> RetryPendingLeaseReleaseBeforeStartAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = Volatile.Read(ref pendingLeaseRelease);
        if (pending is null)
        {
            return null;
        }

        if (pending.IsExpired(now))
        {
            Interlocked.CompareExchange(ref pendingLeaseRelease, null, pending);
            return null;
        }

        var release = await ReleaseLeaseCoreAsync(cancellationToken).ConfigureAwait(false);
        if (release.Status == SupervisorOperationStatus.Applied)
        {
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return release;
        }


        return release;
    }


    private async ValueTask<SupervisorOperationResult> ReleaseLeaseCoreAsync(
        CancellationToken cancellationToken,
        bool processStopped = false)
    {
        var current = Volatile.Read(ref pendingLeaseRelease);
        if (current is null)
        {
            current = Volatile.Read(ref lease);
            if (current is null)
            {
                return Result(
                    SupervisorOperationStatus.Applied,
                    ServiceStateReasonCode.None,
                    Snapshot,
                    processStopped: processStopped);
            }

            Volatile.Write(ref pendingLeaseRelease, current);
            Volatile.Write(ref lease, null);
        }

        initialLeasePending = false;
        PortLeaseOperationResult releaseResult;
        try
        {
            var release = new PortLeaseRelease(
                current.NodeId,
                current.ServiceId,
                current.GenerationId,
                current.Port,
                current.Version);
            releaseResult = await leaseStore.ApplyAsync(
                PortLeaseIntent.ReleaseLease(release),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SupervisionLogMessages.OperationCancelled(_logger, "ReleaseLease", current.ServiceId);
            return Result(
                SupervisorOperationStatus.Cancelled,
                ServiceStateReasonCode.Cancelled,
                Snapshot,
                processStopped: processStopped);
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.LeaseReleaseFailed(
                _logger,
                exception,
                current.NodeId.ToString(),
                current.ServiceId,
                current.Port);
            return Result(
                SupervisorOperationStatus.Failed,
                ServiceStateReasonCode.DatabaseUnavailable,
                Snapshot,
                processStopped: processStopped);
        }

        if (releaseResult.Status is PortLeaseOperationStatus.Applied or PortLeaseOperationStatus.NotFound)
        {
            Interlocked.CompareExchange(ref pendingLeaseRelease, null, current);
            return Result(
                SupervisorOperationStatus.Applied,
                ServiceStateReasonCode.None,
                Snapshot,
                leaseOwnershipLost: true,
                processStopped: processStopped);
        }

        SupervisionLogMessages.LeaseReleaseStatusFailed(
            _logger,
            current.NodeId.ToString(),
            current.ServiceId,
            current.Port,
            releaseResult.Status);
        var (status, reason) = releaseResult.Status switch
        {
            PortLeaseOperationStatus.Conflict => (SupervisorOperationStatus.Conflict, ServiceStateReasonCode.PortLeaseConflict),
            PortLeaseOperationStatus.DatabaseUnavailable => (SupervisorOperationStatus.Unavailable, ServiceStateReasonCode.DatabaseUnavailable),
            PortLeaseOperationStatus.Cancelled => (SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled),
            PortLeaseOperationStatus.Rejected => (SupervisorOperationStatus.Rejected, ServiceStateReasonCode.PortLeaseUnavailable),
            PortLeaseOperationStatus.RetryableTransient => (SupervisorOperationStatus.RetryableTransient, ServiceStateReasonCode.PortLeaseUnavailable),
            PortLeaseOperationStatus.PolicyRejected => (SupervisorOperationStatus.PolicyRejected, ServiceStateReasonCode.PortLeaseUnavailable),
            _ => (SupervisorOperationStatus.Unavailable, ServiceStateReasonCode.PortLeaseUnavailable)
        };
        return Result(status, reason, Snapshot, processStopped: processStopped);
    }


    private ServiceRuntimeSnapshot Exchange(ServiceRuntimeSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Interlocked.Exchange(ref snapshot, next);
        return next;
    }

    private static SupervisorOperationResult Result(
        SupervisorOperationStatus status,
        ServiceStateReasonCode reason,
        ServiceRuntimeSnapshot current,
        PortLease? currentLease = null,
        RestartPlan? restart = null,
        HealthRetryDecision? health = null,
        string? failureMessage = null,
        bool leaseOwnershipLost = false,
        bool processStopped = false) =>
        new(status, reason, current, currentLease, restart, health, failureMessage, leaseOwnershipLost, processStopped);

    /// <summary>Retries pending cleanup before disposing the lifecycle gate.</summary>
    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref pendingLeaseRelease) is not null)
            {
                _ = await ReleaseLeaseCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
        }
    }
}
