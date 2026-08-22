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
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? NormalizeStartedAt(DateTimeOffset? startedAt)
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
        catch
        {
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
            return new ProcessOperationResult(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StopRequested);
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
        var current = Snapshot;
        var next = Exchange(ServiceStateTransition.RecordProcessExit(current, successfulExit, now));
        var policy = next.Desired == DesiredServiceState.Running
            ? restartPolicy
            : ServiceRestartPolicy.Never;
        var plan = RestartPlanner.Plan(policy, successfulExit, next.RestartAttempts, now, restartBackoff, restartJitter, cancellationToken);
        var planned = Exchange(ServiceStateTransition.RecordRestartPlan(next, plan, now));
        return Result(plan.ShouldRestart ? SupervisorOperationStatus.Applied : SupervisorOperationStatus.Rejected, plan.Reason, planned, Lease, plan);
    }

    private async ValueTask ReleaseLeaseBestEffort(CancellationToken cancellationToken)
    {
        var current = Interlocked.Exchange(ref lease, null);
        if (current is null)
        {
            return;
        }

        try
        {
            var release = new PortLeaseRelease(current.NodeId, current.ServiceId, current.Port, current.Version);
            _ = await leaseStore.ApplyAsync(PortLeaseIntent.ReleaseLease(release), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The lease is removed from publication; persistence will expire it safely.
        }
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
        HealthRetryDecision? health = null) =>
        new(status, reason, current, currentLease, restart, health);

    /// <summary>Releases the supervisor lifecycle gate.</summary>
    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleGate.Release();
        lifecycleGate.Dispose();
    }
}
