using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

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
