namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies operations subject to database availability admission.</summary>
public enum SupervisorOperationKind
{
    /// <summary>Start a new service instance.</summary>
    StartService,

    /// <summary>Restart a service instance.</summary>
    RestartService,

    /// <summary>Acquire a new port lease.</summary>
    AcquirePortLease,

    /// <summary>Renew an existing port lease.</summary>
    RenewPortLease,

    /// <summary>Stop an existing service instance.</summary>
    StopService,

    /// <summary>Release an existing port lease.</summary>
    ReleasePortLease,

    /// <summary>Run an observation that does not mutate persistence.</summary>
    ObserveHealth
}

/// <summary>Identifies the availability of required persistence.</summary>
public enum PersistenceAvailability
{
    /// <summary>Persistence is available for gated operations.</summary>
    Available,

    /// <summary>Persistence is known to be unavailable.</summary>
    Unavailable,

    /// <summary>Persistence availability has not been established.</summary>
    Unknown
}

/// <summary>Contains a pure database-gate decision for a supervisor operation.</summary>
public sealed record SupervisorAdmissionDecision
{
    /// <summary>Creates a supervisor admission decision.</summary>
    /// <param name="operation">The operation being admitted.</param>
    /// <param name="availability">The observed persistence availability.</param>
    /// <param name="allowed">Whether the operation may proceed.</param>
    /// <param name="reason">The safe reason code.</param>
    public SupervisorAdmissionDecision(
        SupervisorOperationKind operation,
        PersistenceAvailability availability,
        bool allowed,
        ServiceStateReasonCode reason)
    {
        Operation = operation;
        Availability = availability;
        Allowed = allowed;
        Reason = reason;
    }

    /// <summary>Gets the operation being admitted.</summary>
    public SupervisorOperationKind Operation { get; }

    /// <summary>Gets the persistence availability.</summary>
    public PersistenceAvailability Availability { get; }

    /// <summary>Gets whether the operation may proceed.</summary>
    public bool Allowed { get; }

    /// <summary>Gets the safe gate reason.</summary>
    public ServiceStateReasonCode Reason { get; }
}

/// <summary>Evaluates database admission without accessing a database.</summary>
public static class SupervisorAdmissionPolicy
{
    /// <summary>Evaluates whether a supervisor operation may proceed.</summary>
    /// <param name="operation">The operation to evaluate.</param>
    /// <param name="availability">The current persistence availability.</param>
    /// <returns>A pure gate result.</returns>
    public static SupervisorAdmissionDecision Evaluate(
        SupervisorOperationKind operation,
        PersistenceAvailability availability)
    {
        var mutatingLeaseOrStart = operation is
            SupervisorOperationKind.StartService or
            SupervisorOperationKind.RestartService or
            SupervisorOperationKind.AcquirePortLease or
            SupervisorOperationKind.RenewPortLease;
        var allowed = availability == PersistenceAvailability.Available || !mutatingLeaseOrStart;
        var reason = allowed
            ? ServiceStateReasonCode.None
            : ServiceStateReasonCode.DatabaseUnavailable;
        return new SupervisorAdmissionDecision(operation, availability, allowed, reason);
    }
    
    /// <summary>Determines whether database loss blocks starting or restarting a service.</summary>
    /// <param name="availability">The current persistence availability.</param>
    /// <returns><see langword="true"/> when start and restart are blocked.</returns>
    public static bool BlocksNewOrRestartedServices(PersistenceAvailability availability) =>
        availability != PersistenceAvailability.Available;
}
