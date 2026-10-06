using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Safe loopback endpoint published from an active, unexpired lease.</summary>
public sealed record ServiceLeaseEndpoint
{
    /// <summary>Creates an active service endpoint observation.</summary>
    public ServiceLeaseEndpoint(
        Guid serviceId,
        Guid generationId,
        string nodeId,
        LoopbackEndpoint endpoint,
        DateTimeOffset expiresAt,
        long leaseVersion)
    {
        if (!UuidV7.IsVersion7(generationId))
        {
            throw new ArgumentException("A UUID v7 generation identifier is required.", nameof(generationId));
        }

        ServiceId = serviceId;
        GenerationId = generationId;
        NodeId = nodeId;
        Endpoint = endpoint;
        ExpiresAt = expiresAt;
        LeaseVersion = leaseVersion;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; init; }

    /// <summary>Gets the service generation identifier.</summary>
    public Guid GenerationId { get; init; }

    /// <summary>Gets the owning node identifier.</summary>
    public string NodeId { get; init; }

    /// <summary>Gets the leased loopback endpoint.</summary>
    public LoopbackEndpoint Endpoint { get; init; }

    /// <summary>Gets the lease expiration timestamp.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Gets the optimistic lease version.</summary>
    public long LeaseVersion { get; init; }
}

/// <summary>Read-only endpoint publication boundary for Host startup snapshots.</summary>
public interface IPersistencePortLeaseReader
{
    /// <summary>Reads an active endpoint for one service generation owned by the supplied node.</summary>
    ValueTask<ServiceLeaseEndpoint?> ResolveAsync(Guid serviceId, Guid generationId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>Reads a complete active endpoint snapshot for one node.</summary>
    ValueTask<IReadOnlyList<ServiceLeaseEndpoint>> ReadActiveEndpointsAsync(string nodeId, CancellationToken cancellationToken = default);

}

/// <summary>Durable service runtime and port lease persistence adapter.</summary>
public sealed partial class EfServiceRuntimePersistence : IPersistencePortLeaseReader, IServiceRuntimePersistence
{
    private readonly NekostickDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates the adapter.</summary>
    /// <param name="db">The persistence context.</param>
    /// <param name="timeProvider">The clock used for persisted timestamps.</param>
    /// <param name="logger">The optional persistence logger.</param>
    public EfServiceRuntimePersistence(
        NekostickDbContext db,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<ServiceLeaseEndpoint?> ResolveAsync(
        Guid serviceId,
        Guid generationId,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (serviceId == Guid.Empty || !UuidV7.IsVersion7(generationId) || !IsSafeNodeId(nodeId)) return null;
        try
        {
            var nodeActive = await _db.Nodes.AsNoTracking().AnyAsync(
                value => value.NodeId == nodeId && value.IsActive,
                cancellationToken).ConfigureAwait(false);
            if (!nodeActive) return null;
            var now = _time.GetUtcNow();
            var lease = await _db.PortLeases.AsNoTracking().SingleOrDefaultAsync(
                value => value.ServiceId == serviceId && value.GenerationId == generationId && value.NodeId == nodeId && value.LeaseExpiresAt > now,
                cancellationToken).ConfigureAwait(false);
            return lease is null ? null : new ServiceLeaseEndpoint(serviceId, lease.GenerationId, nodeId, new LoopbackEndpoint(LoopbackAddressKind.IPv4, lease.Port), lease.LeaseExpiresAt, lease.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.RuntimePersistenceCancelled(_logger, "ResolveEndpoint", nodeId, serviceId);
            throw;
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.RuntimePersistenceFailed(_logger, exception, "ResolveEndpoint", nodeId, serviceId);
            return null;
        }
    }
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ServiceLeaseEndpoint>> ReadActiveEndpointsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeNodeId(nodeId)) return Array.Empty<ServiceLeaseEndpoint>();
        try
        {
            var nodeActive = await _db.Nodes.AsNoTracking().AnyAsync(
                value => value.NodeId == nodeId && value.IsActive,
                cancellationToken).ConfigureAwait(false);
            if (!nodeActive) return Array.Empty<ServiceLeaseEndpoint>();
            var now = _time.GetUtcNow();
            var leases = await _db.PortLeases.AsNoTracking()
                .Where(value => value.NodeId == nodeId && value.LeaseExpiresAt > now)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return leases
                .Where(value => value.Port is >= 1 and <= 65535 && value.ServiceId != Guid.Empty)
                .Select(value => new ServiceLeaseEndpoint(
                    value.ServiceId,
                    value.GenerationId,
                    value.NodeId,
                    new LoopbackEndpoint(LoopbackAddressKind.IPv4, value.Port),
                    value.LeaseExpiresAt,
                    value.Version))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.RuntimePersistenceCancelled(_logger, "ReadActiveEndpoints", nodeId, Guid.Empty);
            throw;
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.RuntimePersistenceFailed(_logger, exception, "ReadActiveEndpoints", nodeId, Guid.Empty);
            return Array.Empty<ServiceLeaseEndpoint>();
        }
    }
}
