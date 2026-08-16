using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Describes the lifecycle desired by the immutable supervisor snapshot.</summary>
public enum DesiredServiceState
{
    /// <summary>The service is disabled and must not be started.</summary>
    Disabled,

    /// <summary>The service is enabled but has no required running instance.</summary>
    Stopped,

    /// <summary>The service is enabled and should have a healthy running instance.</summary>
    Running
}

/// <summary>Identifies a safe, non-sensitive reason for a service state.</summary>
public enum ServiceStateReasonCode
{
    /// <summary>No specific reason was recorded.</summary>
    None,

    /// <summary>The service is disabled by desired state.</summary>
    DesiredDisabled,

    /// <summary>The service is enabled but intentionally stopped.</summary>
    DesiredStopped,

    /// <summary>A service start is waiting for an executor or health result.</summary>
    StartRequested,

    /// <summary>The process executor accepted a start.</summary>
    StartAccepted,

    /// <summary>The process executor rejected a start.</summary>
    StartRejected,

    /// <summary>The launch specification is invalid.</summary>
    InvalidLaunchSpecification,

    /// <summary>A health observation has not succeeded yet.</summary>
    HealthPending,

    /// <summary>The latest health observation succeeded.</summary>
    Healthy,

    /// <summary>A health observation failed before the failure threshold.</summary>
    HealthCheckFailed,

    /// <summary>The health failure threshold was reached.</summary>
    HealthFailureThreshold,

    /// <summary>The startup or health deadline expired.</summary>
    HealthTimeout,

    /// <summary>The process exited while it was desired to run.</summary>
    ProcessExited,

    /// <summary>The process exited successfully while it was desired to run.</summary>
    ProcessExitedSuccessfully,

    /// <summary>The configured restart policy does not allow a restart.</summary>
    RestartPolicyDisabled,

    /// <summary>The restart attempt limit was reached.</summary>
    RestartAttemptLimitReached,

    /// <summary>The database gate does not permit a new or restarted service.</summary>
    DatabaseUnavailable,

    /// <summary>The requested port lease was unavailable.</summary>
    PortLeaseUnavailable,

    /// <summary>The requested port lease conflicted with another lease.</summary>
    PortLeaseConflict,

    /// <summary>The port lease expired before the service operation completed.</summary>
    PortLeaseExpired,

    /// <summary>A stop was requested by desired state or replacement.</summary>
    StopRequested,

    /// <summary>The service stopped successfully.</summary>
    StopCompleted,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>A deadline was reached while the desired state was changing.</summary>
    DeadlineExpired,

    /// <summary>An operation was superseded by a newer immutable snapshot.</summary>
    Superseded
}

/// <summary>Identifies the kind of deadline held by a service state snapshot.</summary>
public enum ServiceDeadlineKind
{
    /// <summary>The deadline for the initial health check sequence.</summary>
    StartupHealth,

    /// <summary>The deadline for a graceful process stop.</summary>
    ProcessStop,

    /// <summary>The deadline for a steady-state health result.</summary>
    SteadyHealth,

    /// <summary>The time before a planned restart may be attempted.</summary>
    RestartBackoff
}

/// <summary>Contains an immutable service deadline.</summary>
public readonly record struct ServiceDeadline
{
    /// <summary>Creates a service deadline.</summary>
    /// <param name="kind">The deadline kind.</param>
    /// <param name="at">The UTC deadline instant.</param>
    public ServiceDeadline(ServiceDeadlineKind kind, DateTimeOffset at)
    {
        Kind = kind;
        At = at.ToUniversalTime();
    }

    /// <summary>Gets the deadline kind.</summary>
    public ServiceDeadlineKind Kind { get; }

    /// <summary>Gets the UTC instant at which the deadline is reached.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Determines whether the deadline has been reached.</summary>
    /// <param name="now">The instant to compare with the deadline.</param>
    /// <returns><see langword="true"/> when the deadline is reached.</returns>
    public bool IsReached(DateTimeOffset now) => now.ToUniversalTime() >= At;
}

/// <summary>Contains immutable restart-attempt counters for one service window.</summary>
public readonly record struct RestartAttemptState
{
    /// <summary>Creates restart-attempt state.</summary>
    /// <param name="attempts">The number of attempts in the active window.</param>
    /// <param name="windowStartedAt">The start of the active window, if any.</param>
    /// <param name="lastAttemptAt">The last attempt instant, if any.</param>
    public RestartAttemptState(int attempts, DateTimeOffset? windowStartedAt = null, DateTimeOffset? lastAttemptAt = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);

        if (attempts == 0 && (windowStartedAt.HasValue || lastAttemptAt.HasValue))
        {
            throw new ArgumentException("Zero attempts cannot have attempt timestamps.", nameof(attempts));
        }

        if (attempts > 0 && (!windowStartedAt.HasValue || !lastAttemptAt.HasValue))
        {
            throw new ArgumentException("Attempt timestamps are required when attempts exist.", nameof(attempts));
        }

        if (windowStartedAt.HasValue && lastAttemptAt.HasValue &&
            lastAttemptAt.Value.ToUniversalTime() < windowStartedAt.Value.ToUniversalTime())
        {
            throw new ArgumentException("The last attempt cannot precede the attempt window.", nameof(lastAttemptAt));
        }

        Attempts = attempts;
        WindowStartedAt = windowStartedAt?.ToUniversalTime();
        LastAttemptAt = lastAttemptAt?.ToUniversalTime();
    }

    /// <summary>Gets the number of restart attempts in the active window.</summary>
    public int Attempts { get; }

    /// <summary>Gets the active restart-window start.</summary>
    public DateTimeOffset? WindowStartedAt { get; }

    /// <summary>Gets the last restart-attempt instant.</summary>
    public DateTimeOffset? LastAttemptAt { get; }

    /// <summary>Gets an empty attempt state.</summary>
    public static RestartAttemptState Empty => new(0);

    /// <summary>Determines whether the active window has expired.</summary>
    /// <param name="now">The instant to compare.</param>
    /// <param name="window">The maximum active-window duration.</param>
    /// <returns><see langword="true"/> when a reset is due.</returns>
    public bool IsWindowExpired(DateTimeOffset now, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        return !WindowStartedAt.HasValue || now.ToUniversalTime() - WindowStartedAt.Value >= window;
    }

    /// <summary>Records one restart attempt immutably.</summary>
    /// <param name="at">The UTC attempt instant.</param>
    /// <param name="window">The active-window duration.</param>
    /// <returns>The next immutable attempt state.</returns>
    public RestartAttemptState RecordAttempt(DateTimeOffset at, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        var utc = at.ToUniversalTime();
        return IsWindowExpired(utc, window)
            ? new RestartAttemptState(1, utc, utc)
            : new RestartAttemptState(checked(Attempts + 1), WindowStartedAt, utc);
    }
}

/// <summary>Contains one bounded, non-sensitive health observation.</summary>
public sealed record HealthObservationResult
{
    /// <summary>Creates a health observation result.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="status">The fixed observation outcome.</param>
    /// <param name="observedAt">The UTC observation instant.</param>
    /// <param name="duration">The elapsed probe duration.</param>
    /// <param name="attempt">The one-based attempt number.</param>
    public HealthObservationResult(
        Guid serviceId,
        HealthObservationStatus status,
        DateTimeOffset observedAt,
        TimeSpan duration,
        int attempt)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        ServiceId = serviceId;
        Status = status;
        ObservedAt = observedAt.ToUniversalTime();
        Duration = duration;
        Attempt = attempt;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the fixed outcome without diagnostic text.</summary>
    public HealthObservationStatus Status { get; }

    /// <summary>Gets the UTC observation instant.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Gets the elapsed observation duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets the one-based observation attempt.</summary>
    public int Attempt { get; }

    /// <summary>Gets the corresponding domain health state.</summary>
    public ServiceHealthState HealthState => Status switch
    {
        HealthObservationStatus.Healthy => ServiceHealthState.Healthy,
        HealthObservationStatus.Unhealthy or HealthObservationStatus.TimedOut => ServiceHealthState.Unhealthy,
        _ => ServiceHealthState.Unknown
    };
}

/// <summary>Identifies a health observation outcome without exposing probe details.</summary>
public enum HealthObservationStatus
{
    /// <summary>The health check succeeded.</summary>
    Healthy,

    /// <summary>The health check completed and reported failure.</summary>
    Unhealthy,

    /// <summary>The health check exceeded its permitted duration.</summary>
    TimedOut,

    /// <summary>The health check was cancelled.</summary>
    Cancelled,

    /// <summary>The health check could not be performed.</summary>
    Unavailable
}

/// <summary>Contains immutable desired and observed service state.</summary>
public sealed record ServiceRuntimeSnapshot
{
    /// <summary>Creates an immutable service runtime snapshot.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="desired">The desired lifecycle state.</param>
    /// <param name="observedLifecycle">The observed domain lifecycle state.</param>
    /// <param name="health">The observed domain health state.</param>
    /// <param name="reason">The safe state reason.</param>
    /// <param name="changedAt">The UTC state-change instant.</param>
    /// <param name="lastHealthObservation">The latest safe health result.</param>
    /// <param name="deadline">The active deadline, if any.</param>
    /// <param name="restartAttempts">The immutable restart-attempt state.</param>
    /// <param name="consecutiveHealthFailures">The consecutive health failure count.</param>
    public ServiceRuntimeSnapshot(
        Guid serviceId,
        DesiredServiceState desired,
        ServiceLifecycleState observedLifecycle,
        ServiceHealthState health,
        ServiceStateReasonCode reason,
        DateTimeOffset changedAt,
        HealthObservationResult? lastHealthObservation = null,
        ServiceDeadline? deadline = null,
        RestartAttemptState restartAttempts = default,
        int consecutiveHealthFailures = 0)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveHealthFailures);

        if (lastHealthObservation is not null && lastHealthObservation.ServiceId != serviceId)
        {
            throw new ArgumentException("The health result belongs to another service.", nameof(lastHealthObservation));
        }

        ServiceId = serviceId;
        Desired = desired;
        ObservedLifecycle = observedLifecycle;
        Health = health;
        Reason = reason;
        ChangedAt = changedAt.ToUniversalTime();
        LastHealthObservation = lastHealthObservation;
        Deadline = deadline;
        RestartAttempts = restartAttempts;
        ConsecutiveHealthFailures = consecutiveHealthFailures;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the desired lifecycle state.</summary>
    public DesiredServiceState Desired { get; }

    /// <summary>Gets the observed domain lifecycle state.</summary>
    public ServiceLifecycleState ObservedLifecycle { get; }

    /// <summary>Gets the observed domain health state.</summary>
    public ServiceHealthState Health { get; }

    /// <summary>Gets the safe state reason code.</summary>
    public ServiceStateReasonCode Reason { get; }

    /// <summary>Gets the UTC instant of the latest state transition.</summary>
    public DateTimeOffset ChangedAt { get; }

    /// <summary>Gets the latest safe health result.</summary>
    public HealthObservationResult? LastHealthObservation { get; }

    /// <summary>Gets the active deadline.</summary>
    public ServiceDeadline? Deadline { get; }

    /// <summary>Gets the immutable restart-attempt state.</summary>
    public RestartAttemptState RestartAttempts { get; }

    /// <summary>Gets the consecutive health failure count.</summary>
    public int ConsecutiveHealthFailures { get; }
}

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

    private static ServiceRuntimeSnapshot NewSnapshot(
        ServiceRuntimeSnapshot current,
        DesiredServiceState desired,
        ServiceLifecycleState lifecycle,
        ServiceHealthState health,
        ServiceStateReasonCode reason,
        DateTimeOffset now,
        ServiceDeadline? deadline,
        HealthObservationResult? observation = null,
        int consecutiveHealthFailures = 0) => new(
            current.ServiceId,
            desired,
            lifecycle,
            health,
            reason,
            now,
            observation ?? current.LastHealthObservation,
            deadline,
            current.RestartAttempts,
            consecutiveHealthFailures);
}

/// <summary>Contains only enumerated fields suitable for status and doctor output.</summary>
public sealed record SafeServiceDiagnosis
{
    /// <summary>Creates a safe service diagnosis from a runtime snapshot.</summary>
    /// <param name="snapshot">The immutable runtime snapshot.</param>
    public SafeServiceDiagnosis(ServiceRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ServiceId = snapshot.ServiceId;
        Lifecycle = snapshot.ObservedLifecycle;
        Desired = snapshot.Desired;
        Health = snapshot.Health;
        Reason = snapshot.Reason;
        ConsecutiveHealthFailures = snapshot.ConsecutiveHealthFailures;
        RestartAttempts = snapshot.RestartAttempts.Attempts;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the desired state.</summary>
    public DesiredServiceState Desired { get; }

    /// <summary>Gets the observed lifecycle.</summary>
    public ServiceLifecycleState Lifecycle { get; }

    /// <summary>Gets the observed health.</summary>
    public ServiceHealthState Health { get; }

    /// <summary>Gets the safe state reason.</summary>
    public ServiceStateReasonCode Reason { get; }

    /// <summary>Gets the consecutive health failure count.</summary>
    public int ConsecutiveHealthFailures { get; }

    /// <summary>Gets the restart-attempt count.</summary>
    public int RestartAttempts { get; }
}
