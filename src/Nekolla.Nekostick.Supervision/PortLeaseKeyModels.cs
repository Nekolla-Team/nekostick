namespace Nekolla.Nekostick.Supervision;

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
    /// <param name="port">The fixed TCP port, or zero for automatic allocation.</param>
    /// <param name="timeToLive">The requested TTL.</param>
    /// <param name="expectedVersion">The expected lease version, if any.</param>
    /// <param name="automaticPortRangeStart">The optional inclusive automatic range start.</param>
    /// <param name="automaticPortRangeEnd">The optional inclusive automatic range end.</param>
    public PortLeaseRequest(
        NodeIdentifier nodeId,
        Guid serviceId,
        int port,
        TimeSpan timeToLive,
        long? expectedVersion = null,
        int? automaticPortRangeStart = null,
        int? automaticPortRangeEnd = null)
    {
        ValidateNode(nodeId);
        ValidateServiceAndPort(serviceId, port, automaticPortRangeStart, automaticPortRangeEnd);
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
        AutomaticPortRangeStart = automaticPortRangeStart;
        AutomaticPortRangeEnd = automaticPortRangeEnd;
    }

    /// <summary>Creates an automatic-port request for an optional inclusive range.</summary>
    /// <param name="nodeId">The validated node identifier.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="timeToLive">The requested TTL.</param>
    /// <param name="rangeStart">The optional inclusive range start.</param>
    /// <param name="rangeEnd">The optional inclusive range end.</param>
    /// <param name="expectedVersion">The expected lease version, if any.</param>
    public static PortLeaseRequest Automatic(
        NodeIdentifier nodeId,
        Guid serviceId,
        TimeSpan timeToLive,
        int? rangeStart = null,
        int? rangeEnd = null,
        long? expectedVersion = null) =>
        new(nodeId, serviceId, 0, timeToLive, expectedVersion, rangeStart, rangeEnd);

    /// <summary>Gets the node lease key.</summary>
    public NodeIdentifier NodeId { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the requested fixed TCP port, or zero for automatic allocation.</summary>
    public int Port { get; }

    /// <summary>Gets the requested lease TTL.</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary>Gets the optimistic expected version.</summary>
    public long? ExpectedVersion { get; }

    /// <summary>Gets the optional inclusive automatic range start.</summary>
    public int? AutomaticPortRangeStart { get; }

    /// <summary>Gets the optional inclusive automatic range end.</summary>
    public int? AutomaticPortRangeEnd { get; }

    private static void ValidateServiceAndPort(
        Guid serviceId,
        int port,
        int? automaticPortRangeStart,
        int? automaticPortRangeEnd)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        var automatic = automaticPortRangeStart.HasValue || automaticPortRangeEnd.HasValue;
        if (port != 0 && automatic)
        {
            throw new ArgumentException("Fixed ports cannot specify an automatic range.", nameof(port));
        }

        if (port == 0 && automaticPortRangeStart.HasValue != automaticPortRangeEnd.HasValue)
        {
            throw new ArgumentException("An automatic range requires both bounds.", nameof(automaticPortRangeEnd));
        }

        if (port is not 0 and (< 1 or > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (automaticPortRangeStart is < 1 or > 65535 || automaticPortRangeEnd is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(automaticPortRangeStart));
        }

        if (automaticPortRangeStart.HasValue && automaticPortRangeEnd.HasValue &&
            automaticPortRangeStart > automaticPortRangeEnd)
        {
            throw new ArgumentException("The automatic port range is reversed.", nameof(automaticPortRangeEnd));
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
