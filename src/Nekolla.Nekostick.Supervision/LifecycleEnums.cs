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
