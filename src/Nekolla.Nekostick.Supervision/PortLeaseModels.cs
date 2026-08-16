namespace Nekolla.Nekostick.Supervision;

/// <summary>Contains a validated node identifier for local port lease keys.</summary>
public readonly record struct NodeIdentifier
{
    /// <summary>The maximum node identifier length.</summary>
    public const int MaxLength = 128;

    /// <summary>Creates a validated node identifier.</summary>
    /// <param name="value">The stable node identifier.</param>
    public NodeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("The node identifier is invalid.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the validated identifier text.</summary>
    public string Value { get; }

    /// <summary>Gets whether this value is a valid non-default node identifier.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= MaxLength &&
        !Value.Any(char.IsControl);

    /// <summary>Returns the validated identifier text.</summary>
    /// <returns>The identifier text.</returns>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Contains the immutable default port lease timing policy.</summary>
public sealed record PortLeasePolicy
{
    /// <summary>Creates a port lease policy.</summary>
    /// <param name="timeToLive">The lease TTL.</param>
    /// <param name="renewInterval">The desired renewal interval.</param>
    public PortLeasePolicy(TimeSpan timeToLive, TimeSpan renewInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(renewInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(renewInterval, timeToLive);

        TimeToLive = timeToLive;
        RenewInterval = renewInterval;
    }

    /// <summary>Gets the lease time-to-live.</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary>Gets the desired renewal interval.</summary>
    public TimeSpan RenewInterval { get; }

    /// <summary>Gets the documented 30-second TTL and 10-second renewal policy.</summary>
    public static PortLeasePolicy Default => new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

    /// <summary>Determines whether a lease should be renewed at an instant.</summary>
    /// <param name="lease">The lease to inspect.</param>
    /// <param name="now">The current UTC instant.</param>
    /// <returns><see langword="true"/> when renewal is due.</returns>
    public bool IsRenewalDue(PortLease lease, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return now.ToUniversalTime() >= lease.ExpiresAt - RenewInterval;
    }
}

/// <summary>Contains a validated request to acquire a port lease.</summary>
public sealed record PortLeaseRequest
{
    /// <summary>Creates a port lease request.</summary>
    /// <param name="nodeId">The validated node identifier.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="timeToLive">The requested TTL.</param>
    /// <param name="expectedVersion">The expected lease version, if any.</param>
    public PortLeaseRequest(
        NodeIdentifier nodeId,
        Guid serviceId,
        int port,
        TimeSpan timeToLive,
        long? expectedVersion = null)
    {
        ValidateNode(nodeId);
        ValidateServiceAndPort(serviceId, port);
        ValidateTimeToLive(timeToLive);
        if (expectedVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        TimeToLive = timeToLive;
        ExpectedVersion = expectedVersion;
    }

    /// <summary>Gets the node lease key.</summary>
    public NodeIdentifier NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the requested lease TTL.</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary>Gets the optimistic expected version.</summary>
    public long? ExpectedVersion { get; }

    private static void ValidateServiceAndPort(Guid serviceId, int port)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    private static void ValidateNode(NodeIdentifier nodeId)
    {
        if (!nodeId.IsValid)
        {
            throw new ArgumentException("The node identifier is invalid.", nameof(nodeId));
        }
    }

    private static void ValidateTimeToLive(TimeSpan timeToLive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);
    }
}

/// <summary>Contains a validated request to renew an existing port lease.</summary>
public sealed record PortLeaseRenewal
{
    /// <summary>Creates a port lease renewal request.</summary>
    /// <param name="nodeId">The validated node identifier.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="leaseVersion">The version being renewed.</param>
    /// <param name="timeToLive">The requested new TTL.</param>
    public PortLeaseRenewal(
        NodeIdentifier nodeId,
        Guid serviceId,
        int port,
        long leaseVersion,
        TimeSpan timeToLive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaseVersion);

        _ = new PortLeaseRequest(nodeId, serviceId, port, timeToLive);
        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        LeaseVersion = leaseVersion;
        TimeToLive = timeToLive;
    }

    /// <summary>Gets the node lease key.</summary>
    public NodeIdentifier NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the current lease version.</summary>
    public long LeaseVersion { get; }

    /// <summary>Gets the requested lease TTL.</summary>
    public TimeSpan TimeToLive { get; }
}

/// <summary>Contains a validated request to release a port lease.</summary>
public sealed record PortLeaseRelease
{
    /// <summary>Creates a port lease release request.</summary>
    /// <param name="nodeId">The validated node identifier.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="leaseVersion">The expected version, if any.</param>
    public PortLeaseRelease(NodeIdentifier nodeId, Guid serviceId, int port, long? leaseVersion = null)
    {
        _ = new PortLeaseRequest(nodeId, serviceId, port, TimeSpan.FromTicks(1));
        if (leaseVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseVersion));
        }

        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        LeaseVersion = leaseVersion;
    }

    /// <summary>Gets the node lease key.</summary>
    public NodeIdentifier NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the expected lease version.</summary>
    public long? LeaseVersion { get; }
}

/// <summary>Identifies a port lease operation intent.</summary>
public enum PortLeaseIntentKind
{
    /// <summary>Acquire a new lease.</summary>
    Acquire,

    /// <summary>Release an existing lease.</summary>
    Release,

    /// <summary>Renew an existing lease.</summary>
    Renew
}

/// <summary>Contains one immutable port lease request, release, or renewal intent.</summary>
public sealed record PortLeaseIntent
{
    private PortLeaseIntent(
        PortLeaseIntentKind kind,
        PortLeaseRequest? request,
        PortLeaseRelease? release,
        PortLeaseRenewal? renewal)
    {
        Kind = kind;
        Request = request;
        Release = release;
        Renewal = renewal;
    }

    /// <summary>Creates an acquire intent.</summary>
    /// <param name="request">The immutable lease request.</param>
    /// <returns>An acquire intent.</returns>
    public static PortLeaseIntent Acquire(PortLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(PortLeaseIntentKind.Acquire, request, null, null);
    }

    /// <summary>Creates a release intent.</summary>
    /// <param name="release">The immutable release request.</param>
    /// <returns>A release intent.</returns>
    public static PortLeaseIntent ReleaseLease(PortLeaseRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new(PortLeaseIntentKind.Release, null, release, null);
    }

    /// <summary>Creates a renewal intent.</summary>
    /// <param name="renewal">The immutable renewal request.</param>
    /// <returns>A renewal intent.</returns>
    public static PortLeaseIntent Renew(PortLeaseRenewal renewal)
    {
        ArgumentNullException.ThrowIfNull(renewal);
        return new(PortLeaseIntentKind.Renew, null, null, renewal);
    }

    /// <summary>Gets the operation kind.</summary>
    public PortLeaseIntentKind Kind { get; }

    /// <summary>Gets the acquire request, when this is an acquire intent.</summary>
    public PortLeaseRequest? Request { get; }

    /// <summary>Gets the release request, when this is a release intent.</summary>
    public PortLeaseRelease? Release { get; }

    /// <summary>Gets the renewal request, when this is a renewal intent.</summary>
    public PortLeaseRenewal? Renewal { get; }
}

/// <summary>Contains a validated immutable port lease observation.</summary>
public sealed record PortLease
{
    /// <summary>Creates a port lease observation.</summary>
    /// <param name="nodeId">The validated node identifier.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="acquiredAt">The UTC acquisition instant.</param>
    /// <param name="expiresAt">The UTC expiration instant.</param>
    /// <param name="version">The immutable lease version.</param>
    public PortLease(
        NodeIdentifier nodeId,
        Guid serviceId,
        int port,
        DateTimeOffset acquiredAt,
        DateTimeOffset expiresAt,
        long version)
    {
        _ = new PortLeaseRequest(nodeId, serviceId, port, TimeSpan.FromTicks(1));
        if (expiresAt.ToUniversalTime() <= acquiredAt.ToUniversalTime())
        {
            throw new ArgumentException("A lease must expire after acquisition.", nameof(expiresAt));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(version);

        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        AcquiredAt = acquiredAt.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
        Version = version;
    }

    /// <summary>Gets the node lease key.</summary>
    public NodeIdentifier NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the UTC acquisition instant.</summary>
    public DateTimeOffset AcquiredAt { get; }

    /// <summary>Gets the UTC expiration instant.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets the optimistic lease version.</summary>
    public long Version { get; }

    /// <summary>Determines whether the lease is expired at an instant.</summary>
    /// <param name="now">The current UTC instant.</param>
    /// <returns><see langword="true"/> when expired.</returns>
    public bool IsExpired(DateTimeOffset now) => now.ToUniversalTime() >= ExpiresAt;
}

/// <summary>Identifies the result of applying a port lease intent.</summary>
public enum PortLeaseOperationStatus
{
    /// <summary>The intent succeeded.</summary>
    Applied,

    /// <summary>The lease conflicted with an existing lease.</summary>
    Conflict,

    /// <summary>The lease was not found.</summary>
    NotFound,

    /// <summary>The persistence gate is unavailable.</summary>
    DatabaseUnavailable,

    /// <summary>The intent was cancelled.</summary>
    Cancelled,

    /// <summary>The intent was rejected by validation or concurrency.</summary>
    Rejected
}

/// <summary>Contains a safe result from a future port lease store.</summary>
public sealed record PortLeaseOperationResult
{
    /// <summary>Creates a port lease operation result.</summary>
    /// <param name="status">The fixed operation status.</param>
    /// <param name="lease">The resulting lease, when available.</param>
    public PortLeaseOperationResult(PortLeaseOperationStatus status, PortLease? lease = null)
    {
        if (status == PortLeaseOperationStatus.Applied && lease is null)
        {
            throw new ArgumentException("An applied lease operation requires a lease.", nameof(lease));
        }

        Lease = lease;
        Status = status;
    }

    /// <summary>Gets the fixed operation status.</summary>
    public PortLeaseOperationStatus Status { get; }

    /// <summary>Gets the resulting lease when the operation applied.</summary>
    public PortLease? Lease { get; }
}

/// <summary>Defines the narrow persistence boundary for port lease intents.</summary>
public interface IPortLeaseStore
{
    /// <summary>Applies one validated lease intent through a future persistence adapter.</summary>
    /// <param name="intent">The immutable operation intent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe operation result without database details.</returns>
    ValueTask<PortLeaseOperationResult> ApplyAsync(
        PortLeaseIntent intent,
        CancellationToken cancellationToken = default);
}

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
