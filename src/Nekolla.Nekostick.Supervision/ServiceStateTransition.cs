using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Provides pure state transitions for one service snapshot.</summary>
public static class ServiceStateTransition
{
    /// <summary>Creates the initial immutable snapshot for a service.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="desired">The initial desired state.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>A new service snapshot.</returns>
    public static ServiceRuntimeSnapshot CreateInitial(
        Guid serviceId,
        DesiredServiceState desired,
        DateTimeOffset now)
    {
        var lifecycle = desired == DesiredServiceState.Running
            ? ServiceLifecycleState.Starting
            : ServiceLifecycleState.Disabled;
        var reason = desired switch
        {
            DesiredServiceState.Disabled => ServiceStateReasonCode.DesiredDisabled,
            DesiredServiceState.Stopped => ServiceStateReasonCode.DesiredStopped,
            _ => ServiceStateReasonCode.StartRequested
        };
        return new ServiceRuntimeSnapshot(
            serviceId,
            desired,
            lifecycle,
            ServiceHealthState.Unknown,
            reason,
            now);
    }

    /// <summary>Changes desired state without mutating the prior snapshot.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="desired">The next desired state.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot SetDesiredState(
        ServiceRuntimeSnapshot current,
        DesiredServiceState desired,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (desired == DesiredServiceState.Running)
        {
            return NewSnapshot(
                current,
                desired,
                ServiceLifecycleState.Starting,
                ServiceHealthState.Unknown,
                ServiceStateReasonCode.StartRequested,
                now,
                deadline: null,
                consecutiveHealthFailures: 0);
        }

        var stopping = current.ObservedLifecycle is ServiceLifecycleState.Starting or ServiceLifecycleState.Running;
        return NewSnapshot(
            current,
            desired,
            stopping ? ServiceLifecycleState.Stopping : ServiceLifecycleState.Disabled,
            ServiceHealthState.Unknown,
            desired == DesiredServiceState.Disabled
                ? ServiceStateReasonCode.DesiredDisabled
                : ServiceStateReasonCode.DesiredStopped,
            now,
            deadline: null,
            consecutiveHealthFailures: 0);
    }

    /// <summary>Records that a start was requested with an optional deadline.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="deadline">The startup deadline.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordStartRequested(
        ServiceRuntimeSnapshot current,
        ServiceDeadline? deadline,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Desired != DesiredServiceState.Running)
        {
            return current;
        }

        return NewSnapshot(
            current,
            current.Desired,
            ServiceLifecycleState.Starting,
            ServiceHealthState.Unknown,
            ServiceStateReasonCode.StartRequested,
            now,
            deadline,
            consecutiveHealthFailures: 0);
    }

    /// <summary>Records cancellation of a start operation without mutating the prior snapshot.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordStartCancelled(
        ServiceRuntimeSnapshot current,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Desired != DesiredServiceState.Running)
        {
            return current;
        }

        return NewSnapshot(
            current,
            current.Desired,
            ServiceLifecycleState.Failed,
            ServiceHealthState.Unknown,
            ServiceStateReasonCode.Cancelled,
            now,
            deadline: null,
            consecutiveHealthFailures: 0);
    }

    /// <summary>Records a process executor start result.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="accepted">Whether the executor accepted the start.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordStartResult(
        ServiceRuntimeSnapshot current,
        bool accepted,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Desired != DesiredServiceState.Running)
        {
            return current;
        }

        return NewSnapshot(
            current,
            current.Desired,
            accepted ? ServiceLifecycleState.Starting : ServiceLifecycleState.Failed,
            ServiceHealthState.Unknown,
            accepted ? ServiceStateReasonCode.StartAccepted : ServiceStateReasonCode.StartRejected,
            now,
            current.Deadline,
            consecutiveHealthFailures: 0);
    }

    /// <summary>Records a health result using a consecutive-failure threshold.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="observation">The safe health result.</param>
    /// <param name="failureThreshold">The number of failures that marks the service failed.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordHealthObservation(
        ServiceRuntimeSnapshot current,
        HealthObservationResult observation,
        int failureThreshold,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        if (observation.ServiceId != current.ServiceId)
        {
            throw new ArgumentException("The health result belongs to another service.", nameof(observation));
        }

        if (observation.Status == HealthObservationStatus.Healthy)
        {
            return NewSnapshot(
                current,
                current.Desired,
                current.Desired == DesiredServiceState.Running
                    ? ServiceLifecycleState.Running
                    : ServiceLifecycleState.Disabled,
                ServiceHealthState.Healthy,
                ServiceStateReasonCode.Healthy,
                now,
                deadline: null,
                observation: observation,
                consecutiveHealthFailures: 0);
        }

        var failures = checked(current.ConsecutiveHealthFailures + 1);
        var terminal = failures >= failureThreshold ||
            current.Deadline is { } deadline && deadline.IsReached(observation.ObservedAt);
        var reason = terminal
            ? current.Deadline is { } expired && expired.IsReached(observation.ObservedAt)
                ? ServiceStateReasonCode.HealthTimeout
                : ServiceStateReasonCode.HealthFailureThreshold
            : observation.Status == HealthObservationStatus.Cancelled
                ? ServiceStateReasonCode.Cancelled
                : ServiceStateReasonCode.HealthCheckFailed;

        return NewSnapshot(
            current,
            current.Desired,
            terminal ? ServiceLifecycleState.Failed : current.ObservedLifecycle,
            observation.HealthState,
            reason,
            now,
            deadline: current.Deadline,
            observation: observation,
            consecutiveHealthFailures: failures);
    }

    /// <summary>Records a process exit without exposing process output or handles.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="successful">Whether the process exit was successful.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordProcessExit(
        ServiceRuntimeSnapshot current,
        bool successful,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Desired != DesiredServiceState.Running)
        {
            return NewSnapshot(
                current,
                current.Desired,
                ServiceLifecycleState.Disabled,
                ServiceHealthState.Unknown,
                ServiceStateReasonCode.StopCompleted,
                now,
                deadline: null,
                consecutiveHealthFailures: 0);
        }

        return NewSnapshot(
            current,
            current.Desired,
            ServiceLifecycleState.Failed,
            ServiceHealthState.Unhealthy,
            successful ? ServiceStateReasonCode.ProcessExitedSuccessfully : ServiceStateReasonCode.ProcessExited,
            now,
            deadline: null,
            consecutiveHealthFailures: current.ConsecutiveHealthFailures);
    }

    /// <summary>Records a stop request.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordStopRequested(
        ServiceRuntimeSnapshot current,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        return NewSnapshot(
            current,
            current.Desired,
            ServiceLifecycleState.Stopping,
            ServiceHealthState.Unknown,
            ServiceStateReasonCode.StopRequested,
            now,
            deadline: current.Deadline,
            consecutiveHealthFailures: current.ConsecutiveHealthFailures);
    }

    /// <summary>Records a completed stop.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordStopped(
        ServiceRuntimeSnapshot current,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        var lifecycle = current.Desired == DesiredServiceState.Running
            ? ServiceLifecycleState.Starting
            : ServiceLifecycleState.Disabled;
        return NewSnapshot(
            current,
            current.Desired,
            lifecycle,
            ServiceHealthState.Unknown,
            current.Desired == DesiredServiceState.Running
                ? ServiceStateReasonCode.StartRequested
                : ServiceStateReasonCode.StopCompleted,
            now,
            deadline: null,
            consecutiveHealthFailures: 0);
    }

    /// <summary>Applies an expired deadline as a pure state transition.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="now">The UTC instant used to evaluate the deadline.</param>
    /// <returns>The original or next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot ApplyDeadline(
        ServiceRuntimeSnapshot current,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Deadline is not { } deadline || !deadline.IsReached(now))
        {
            return current;
        }

        return NewSnapshot(
            current,
            current.Desired,
            current.Desired == DesiredServiceState.Running
                ? ServiceLifecycleState.Failed
                : ServiceLifecycleState.Disabled,
            current.Desired == DesiredServiceState.Running
                ? ServiceHealthState.Unhealthy
                : ServiceHealthState.Unknown,
            ServiceStateReasonCode.DeadlineExpired,
            now,
            deadline: null,
            consecutiveHealthFailures: current.ConsecutiveHealthFailures);
    }

    /// <summary>Applies a pure restart plan and records its immutable attempt state.</summary>
    /// <param name="current">The current immutable snapshot.</param>
    /// <param name="plan">The safe restart plan.</param>
    /// <param name="now">The UTC transition instant.</param>
    /// <returns>The next immutable snapshot.</returns>
    public static ServiceRuntimeSnapshot RecordRestartPlan(
        ServiceRuntimeSnapshot current,
        RestartPlan plan,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ShouldRestart && current.Desired == DesiredServiceState.Running)
        {
            var deadline = plan.NotBefore is { } notBefore
                ? new ServiceDeadline(ServiceDeadlineKind.RestartBackoff, notBefore)
                : (ServiceDeadline?)null;
            return NewSnapshot(
                current,
                current.Desired,
                ServiceLifecycleState.Starting,
                ServiceHealthState.Unknown,
                ServiceStateReasonCode.ProcessExited,
                now,
                deadline,
                observation: null,
                consecutiveHealthFailures: 0,
                restartAttempts: plan.NextAttemptState);
        }

        return NewSnapshot(
            current,
            current.Desired,
            current.Desired == DesiredServiceState.Running
                ? ServiceLifecycleState.Failed
                : ServiceLifecycleState.Disabled,
            current.Desired == DesiredServiceState.Running
                ? ServiceHealthState.Unhealthy
                : ServiceHealthState.Unknown,
            plan.Reason,
            now,
            deadline: null,
            observation: null,
            consecutiveHealthFailures: current.ConsecutiveHealthFailures,
            restartAttempts: plan.NextAttemptState);
    }

    private static ServiceRuntimeSnapshot NewSnapshot(
        ServiceRuntimeSnapshot current,
        DesiredServiceState desired,
        ServiceLifecycleState lifecycle,
        ServiceHealthState health,
        ServiceStateReasonCode reason,
        DateTimeOffset now,
        ServiceDeadline? deadline,
        HealthObservationResult? observation = null,
        int consecutiveHealthFailures = 0,
        RestartAttemptState? restartAttempts = null) => new(
            current.ServiceId,
            desired,
            lifecycle,
            health,
            reason,
            now,
            observation ?? current.LastHealthObservation,
            deadline,
            restartAttempts ?? current.RestartAttempts,
            consecutiveHealthFailures);
}
