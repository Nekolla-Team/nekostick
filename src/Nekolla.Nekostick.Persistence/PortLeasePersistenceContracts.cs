using System.Collections.Immutable;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Identifies the fixed, safe result of a port lease mutation.</summary>
public enum PersistencePortLeaseOperationStatus
{
    /// <summary>The mutation committed.</summary>
    Applied,

    /// <summary>The requested port or lease version conflicts with current state.</summary>
    Conflict,

    /// <summary>The requested lease does not exist or has expired.</summary>
    NotFound,

    /// <summary>The persistence store could not complete the operation.</summary>
    DatabaseUnavailable,

    /// <summary>The operation was cancelled before a safe result was available.</summary>
    Cancelled,

    /// <summary>The request was not valid for this boundary.</summary>
    Rejected
}

/// <summary>Contains the safe public state of one persisted port lease.</summary>
public sealed record PersistencePortLease
{
    /// <summary>Creates a safe lease snapshot.</summary>
    public PersistencePortLease(
        string nodeId,
        Guid serviceId,
        int port,
        DateTimeOffset acquiredAt,
        DateTimeOffset expiresAt,
        long version)
    {
        if (!IsSafeNodeId(nodeId))
        {
            throw new ArgumentException("The node identifier is invalid.", nameof(nodeId));
        }

        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

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

    /// <summary>Gets the owning node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the leased TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the acquisition timestamp in UTC.</summary>
    public DateTimeOffset AcquiredAt { get; }

    /// <summary>Gets the expiration timestamp in UTC.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets the optimistic lease version.</summary>
    public long Version { get; }

    /// <summary>Determines whether the lease is active at the supplied instant.</summary>
    public bool IsActive(DateTimeOffset now) => now.ToUniversalTime() < ExpiresAt;

    internal static bool IsSafeNodeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => !char.IsControl(character));
}

/// <summary>Contains a validated acquire request for the Persistence lease boundary.</summary>
public sealed record PersistencePortLeaseAcquireRequest
{
    /// <summary>Creates an acquire request. Port zero selects the configured automatic range.</summary>
    public PersistencePortLeaseAcquireRequest(
        string nodeId,
        Guid serviceId,
        int port,
        TimeSpan timeToLive,
        long? expectedVersion = null,
        int? automaticPortRangeStart = null,
        int? automaticPortRangeEnd = null)
    {
        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        TimeToLive = timeToLive;
        ExpectedVersion = expectedVersion;
        AutomaticPortRangeStart = automaticPortRangeStart;
        AutomaticPortRangeEnd = automaticPortRangeEnd;
    }

    /// <summary>Creates an automatic-port acquire request for an explicit inclusive range.</summary>
    public static PersistencePortLeaseAcquireRequest Automatic(
        string nodeId,
        Guid serviceId,
        TimeSpan timeToLive,
        int? rangeStart = null,
        int? rangeEnd = null,
        long? expectedVersion = null) =>
        new(nodeId, serviceId, 0, timeToLive, expectedVersion, rangeStart, rangeEnd);

    /// <summary>Gets the owning node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the requested fixed port, or zero for automatic allocation.</summary>
    public int Port { get; }

    /// <summary>Gets the requested lease time-to-live.</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary>Gets the expected current version, or null for an unconditioned acquire.</summary>
    public long? ExpectedVersion { get; }

    /// <summary>Gets the optional inclusive automatic range start.</summary>
    public int? AutomaticPortRangeStart { get; }

    /// <summary>Gets the optional inclusive automatic range end.</summary>
    public int? AutomaticPortRangeEnd { get; }
}

/// <summary>Contains a validated renewal request for a persisted lease.</summary>
public sealed record PersistencePortLeaseRenewRequest
{
    /// <summary>Creates a renewal request.</summary>
    public PersistencePortLeaseRenewRequest(
        string nodeId,
        Guid serviceId,
        int port,
        long leaseVersion,
        TimeSpan timeToLive)
    {
        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        LeaseVersion = leaseVersion;
        TimeToLive = timeToLive;
    }

    /// <summary>Gets the owning node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the leased TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the expected current version.</summary>
    public long LeaseVersion { get; }

    /// <summary>Gets the requested new lease time-to-live.</summary>
    public TimeSpan TimeToLive { get; }
}

/// <summary>Contains a validated release request for a persisted lease.</summary>
public sealed record PersistencePortLeaseReleaseRequest
{
    /// <summary>Creates a release request.</summary>
    public PersistencePortLeaseReleaseRequest(
        string nodeId,
        Guid serviceId,
        int port,
        long? leaseVersion = null)
    {
        NodeId = nodeId;
        ServiceId = serviceId;
        Port = port;
        LeaseVersion = leaseVersion;
    }

    /// <summary>Gets the owning node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the leased TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the expected current version, when supplied.</summary>
    public long? LeaseVersion { get; }
}

/// <summary>Contains a safe fixed outcome from one lease mutation.</summary>
public sealed record PersistencePortLeaseOperationResult
{
    /// <summary>Creates a lease mutation result.</summary>
    public PersistencePortLeaseOperationResult(
        PersistencePortLeaseOperationStatus status,
        PersistencePortLease? lease = null)
    {
        if (status == PersistencePortLeaseOperationStatus.Applied && lease is null)
        {
            throw new ArgumentException("An applied mutation requires a lease snapshot.", nameof(lease));
        }

        Status = status;
        Lease = lease;
    }

    /// <summary>Gets the fixed operation status.</summary>
    public PersistencePortLeaseOperationStatus Status { get; }

    /// <summary>Gets the resulting lease snapshot, when one is available.</summary>
    public PersistencePortLease? Lease { get; }

    /// <summary>Creates a safe unavailable result.</summary>
    public static PersistencePortLeaseOperationResult Unavailable() =>
        new(PersistencePortLeaseOperationStatus.DatabaseUnavailable);

    /// <summary>Creates a safe cancelled result.</summary>
    public static PersistencePortLeaseOperationResult Cancelled() =>
        new(PersistencePortLeaseOperationStatus.Cancelled);
}

/// <summary>Identifies the fixed result of an active lease snapshot read.</summary>
public enum PersistencePortLeaseSnapshotStatus
{
    /// <summary>The complete snapshot was read successfully.</summary>
    Available,

    /// <summary>The persistence store was unavailable.</summary>
    DatabaseUnavailable,

    /// <summary>The read was cancelled.</summary>
    Cancelled,

    /// <summary>The node identifier was rejected.</summary>
    Rejected
}

/// <summary>Contains a complete immutable active-lease snapshot result.</summary>
public sealed record PersistencePortLeaseSnapshotResult
{
    /// <summary>Creates a snapshot result.</summary>
    public PersistencePortLeaseSnapshotResult(
        PersistencePortLeaseSnapshotStatus status,
        ImmutableArray<PersistencePortLease> leases = default)
    {
        Status = status;
        Leases = leases.IsDefault ? ImmutableArray<PersistencePortLease>.Empty : leases;
    }

    /// <summary>Gets the fixed snapshot status.</summary>
    public PersistencePortLeaseSnapshotStatus Status { get; }

    /// <summary>Gets the complete immutable lease collection.</summary>
    public ImmutableArray<PersistencePortLease> Leases { get; }

    /// <summary>Gets whether the collection is a usable database-backed snapshot.</summary>
    public bool IsAvailable => Status == PersistencePortLeaseSnapshotStatus.Available;
}

/// <summary>Defines the Persistence-owned transactional lease boundary.</summary>
public interface IPersistencePortLeaseStore
{
    /// <summary>Acquires one node-owned port lease transactionally.</summary>
    ValueTask<PersistencePortLeaseOperationResult> AcquireAsync(
        PersistencePortLeaseAcquireRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Renews one node-owned port lease with an optimistic version check.</summary>
    ValueTask<PersistencePortLeaseOperationResult> RenewAsync(
        PersistencePortLeaseRenewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases one node-owned port lease with an optional version check.</summary>
    ValueTask<PersistencePortLeaseOperationResult> ReleaseAsync(
        PersistencePortLeaseReleaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the complete active lease snapshot for one node.</summary>
    ValueTask<PersistencePortLeaseSnapshotResult> ReadActiveAsync(
        string nodeId,
        CancellationToken cancellationToken = default);
}
