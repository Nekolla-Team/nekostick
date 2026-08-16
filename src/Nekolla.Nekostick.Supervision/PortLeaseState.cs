namespace Nekolla.Nekostick.Supervision;

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
