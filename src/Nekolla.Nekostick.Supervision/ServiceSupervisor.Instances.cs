using System.Threading;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

public sealed partial class ServiceSupervisor
{
    /// <summary>Gets the opaque active process generation, when the adapter supplies one.</summary>
    public ProcessInstanceId? ActiveProcessInstance => Volatile.Read(ref processInstance)?.Id;

    private sealed record ProcessInstanceHolder(ProcessInstanceId Id);

    private void RememberProcessInstance(ProcessOperationResult result, bool accepted)
    {
        if (accepted && result.InstanceId is { } instanceId)
        {
            Volatile.Write(ref processInstance, new ProcessInstanceHolder(instanceId));
        }
    }

    private async ValueTask<ProcessOperationResult> StopProcessAsync(CancellationToken cancellationToken)
    {
        var active = Volatile.Read(ref processInstance);
        ProcessOperationResult result;
        if (active is not null)
        {
            if (processExecutor is not IProcessInstanceExecutor instanceExecutor)
            {
                return new ProcessOperationResult(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StopRequested);
            }

            result = await instanceExecutor.StopAsync(active.Id, stopGracePeriod, cancellationToken).ConfigureAwait(false);
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
            Interlocked.Exchange(ref processInstance, null);
        }

        return result;
    }

    private void ClearProcessInstance() => Interlocked.Exchange(ref processInstance, null);
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
