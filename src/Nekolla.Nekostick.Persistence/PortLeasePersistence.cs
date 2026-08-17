using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Implements the transactional PostgreSQL lease boundary with safe outcomes.</summary>
public sealed class EfPortLeaseStore : IPersistencePortLeaseStore, IAsyncDisposable
{
    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;
    private readonly NekostickDbContext _db;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the lease store.</summary>
    public EfPortLeaseStore(NekostickDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
    /// <inheritdoc />
    public async ValueTask<PersistencePortLeaseOperationResult> AcquireAsync(
        PersistencePortLeaseAcquireRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !IsValidAcquire(request))
        {
            return new(PersistencePortLeaseOperationStatus.Rejected);
        }

        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow().ToUniversalTime();
            if (!await HasUsableOwnerAsync(request.NodeId, request.ServiceId, cancellationToken).ConfigureAwait(false))
            {
                return new(PersistencePortLeaseOperationStatus.Rejected);
            }

            var existingForService = await _db.PortLeases
                .SingleOrDefaultAsync(value => value.NodeId == request.NodeId && value.ServiceId == request.ServiceId, cancellationToken)
                .ConfigureAwait(false);
            if (existingForService is not null && existingForService.LeaseExpiresAt > now)
            {
                return new(PersistencePortLeaseOperationStatus.Conflict);
            }

            if (existingForService is not null && request.ExpectedVersion is not null &&
                existingForService.Version != request.ExpectedVersion.Value)
            {
                return new(PersistencePortLeaseOperationStatus.Conflict);
            }

            if (existingForService is null && request.ExpectedVersion is not null)
            {
                return new(PersistencePortLeaseOperationStatus.Conflict);
            }

            if (await ReclaimExpiredAsync(request.NodeId, now, cancellationToken).ConfigureAwait(false))
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var port = await SelectPortAsync(request, request.NodeId, cancellationToken).ConfigureAwait(false);
            if (port is null)
            {
                return new(PersistencePortLeaseOperationStatus.Conflict);
            }

            var leaseExpiresAt = TryGetExpiry(now, request.TimeToLive);
            if (leaseExpiresAt is null)
            {
                return new(PersistencePortLeaseOperationStatus.Rejected);
            }

            var entity = new PortLease
            {
                Id = Guid.CreateVersion7(),
                NodeId = request.NodeId,
                Port = port.Value,
                ServiceId = request.ServiceId,
                LeaseExpiresAt = leaseExpiresAt.Value,
                RenewedAt = now,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.PortLeases.Add(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Applied(ToSnapshot(entity));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PersistencePortLeaseOperationResult.Cancelled();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsLeaseConflict(exception))
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }
        catch (Exception)
        {
            return PersistencePortLeaseOperationResult.Unavailable();
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<PersistencePortLeaseOperationResult> RenewAsync(
        PersistencePortLeaseRenewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !IsValidRenew(request))
        {
            return new(PersistencePortLeaseOperationStatus.Rejected);
        }

        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow().ToUniversalTime();
            if (!await HasUsableOwnerAsync(request.NodeId, request.ServiceId, cancellationToken).ConfigureAwait(false))
            {
                return new(PersistencePortLeaseOperationStatus.Rejected);
            }

            if (await ReclaimExpiredAsync(request.NodeId, now, cancellationToken).ConfigureAwait(false))
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var entity = await _db.PortLeases.SingleOrDefaultAsync(
                value => value.NodeId == request.NodeId && value.ServiceId == request.ServiceId && value.Port == request.Port,
                cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                return new(PersistencePortLeaseOperationStatus.NotFound);
            }

            if (entity.Version != request.LeaseVersion)
            {
                return new(PersistencePortLeaseOperationStatus.Conflict);
            }

            var leaseExpiresAt = TryGetExpiry(now, request.TimeToLive);
            if (leaseExpiresAt is null || entity.Version == long.MaxValue)
            {
                return new(PersistencePortLeaseOperationStatus.Rejected);
            }

            entity.LeaseExpiresAt = leaseExpiresAt.Value;
            entity.RenewedAt = now;
            entity.Version++;
            entity.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Applied(ToSnapshot(entity));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PersistencePortLeaseOperationResult.Cancelled();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsLeaseConflict(exception))
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }
        catch (Exception)
        {
            return PersistencePortLeaseOperationResult.Unavailable();
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<PersistencePortLeaseOperationResult> ReleaseAsync(
        PersistencePortLeaseReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !IsValidRelease(request))
        {
            return new(PersistencePortLeaseOperationStatus.Rejected);
        }

        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow().ToUniversalTime();
            if (!await HasUsableOwnerAsync(request.NodeId, request.ServiceId, cancellationToken).ConfigureAwait(false))
            {
                return new(PersistencePortLeaseOperationStatus.Rejected);
            }

            if (await ReclaimExpiredAsync(request.NodeId, now, cancellationToken).ConfigureAwait(false))
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var entity = await _db.PortLeases.SingleOrDefaultAsync(
                value => value.NodeId == request.NodeId && value.ServiceId == request.ServiceId && value.Port == request.Port,
                cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                return new(PersistencePortLeaseOperationStatus.NotFound);
            }

            if (request.LeaseVersion is not null && entity.Version != request.LeaseVersion.Value)
            {
                return new(PersistencePortLeaseOperationStatus.Conflict);
            }

            var snapshot = ToSnapshot(entity);
            _db.PortLeases.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Applied(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PersistencePortLeaseOperationResult.Cancelled();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsLeaseConflict(exception))
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }
        catch (Exception)
        {
            return PersistencePortLeaseOperationResult.Unavailable();
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<PersistencePortLeaseSnapshotResult> ReadActiveAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (!PersistencePortLease.IsSafeNodeId(nodeId))
        {
            return new(PersistencePortLeaseSnapshotStatus.Rejected);
        }

        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken).ConfigureAwait(false);
            var nodeActive = await _db.Nodes.AsNoTracking().AnyAsync(
                value => value.NodeId == nodeId && value.IsActive,
                cancellationToken).ConfigureAwait(false);
            if (!nodeActive)
            {
                return new(PersistencePortLeaseSnapshotStatus.Rejected);
            }

            var now = _time.GetUtcNow().ToUniversalTime();
            var leases = await _db.PortLeases
                .AsNoTracking()
                .Where(value => value.NodeId == nodeId && value.LeaseExpiresAt > now)
                .OrderBy(value => value.ServiceId).ThenBy(value => value.Port)
                .Select(value => new PersistencePortLease(
                    value.NodeId,
                    value.ServiceId,
                    value.Port,
                    value.CreatedAt,
                    value.LeaseExpiresAt,
                    value.Version))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(PersistencePortLeaseSnapshotStatus.Available, leases.ToImmutableArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(PersistencePortLeaseSnapshotStatus.Cancelled);
        }
        catch (Exception)
        {
            return new(PersistencePortLeaseSnapshotStatus.DatabaseUnavailable);
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private async Task<bool> HasUsableOwnerAsync(
        string nodeId,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        var nodeExists = await _db.Nodes.AsNoTracking().AnyAsync(
            value => value.NodeId == nodeId && value.IsActive,
            cancellationToken).ConfigureAwait(false);
        if (!nodeExists)
        {
            return false;
        }

        return await _db.Services.AsNoTracking().AnyAsync(
            value => value.Id == serviceId && value.Enabled,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReclaimExpiredAsync(
        string nodeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = await _db.PortLeases
            .Where(value => value.NodeId == nodeId && value.LeaseExpiresAt <= now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expired.Count == 0)
        {
            return false;
        }

        _db.PortLeases.RemoveRange(expired);
        return true;
    }

    private async Task<int?> SelectPortAsync(
        PersistencePortLeaseAcquireRequest request,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (request.Port is >= MinimumPort and <= MaximumPort)
        {
            var occupied = await _db.PortLeases.AnyAsync(
                value => value.NodeId == nodeId && value.Port == request.Port,
                cancellationToken).ConfigureAwait(false);
            return occupied ? null : request.Port;
        }

        if (request.Port != 0 ||
            (request.AutomaticPortRangeStart is null) != (request.AutomaticPortRangeEnd is null))
        {
            return null;
        }

        var start = request.AutomaticPortRangeStart;
        var end = request.AutomaticPortRangeEnd;
        if (start is null || end is null)
        {
            var settings = await _db.GlobalSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                return null;
            }

            start = settings.AutoPortRangeStart;
            end = settings.AutoPortRangeEnd;
        }

        if (start is < MinimumPort or > MaximumPort ||
            end is < MinimumPort or > MaximumPort ||
            start > end)
        {
            return null;
        }

        var count = (long)end.Value - start.Value + 1;
        if (count is <= 0 or > MaximumPort)
        {
            return null;
        }

        var occupiedPorts = await _db.PortLeases
            .Where(value => value.NodeId == nodeId && value.Port >= start && value.Port <= end)
            .Select(value => value.Port)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
        var offsetStart = ComputeScanOffset(nodeId, request.ServiceId, count);
        for (long offset = 0; offset < count; offset++)
        {
            var candidate = start.Value + (int)((offsetStart + offset) % count);
            if (!occupiedPorts.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static int ComputeScanOffset(string nodeId, Guid serviceId, long count)
    {
        var hash = unchecked((uint)StringComparer.Ordinal.GetHashCode(nodeId));
        hash ^= unchecked((uint)serviceId.GetHashCode());
        return (int)(hash % (uint)count);
    }

    private static PersistencePortLeaseOperationResult Applied(PersistencePortLease lease) =>
        new(PersistencePortLeaseOperationStatus.Applied, lease);

    private static PersistencePortLease ToSnapshot(PortLease value) =>
        new(value.NodeId, value.ServiceId, value.Port, value.CreatedAt, value.LeaseExpiresAt, value.Version);

    private static DateTimeOffset? TryGetExpiry(DateTimeOffset now, TimeSpan ttl)
    {
        try
        {
            return now + ttl;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsValidAcquire(PersistencePortLeaseAcquireRequest request)
    {
        var validPort = request.Port == 0 || request.Port is >= MinimumPort and <= MaximumPort;
        var rangeSpecified = request.AutomaticPortRangeStart is not null || request.AutomaticPortRangeEnd is not null;
        var validRange = !rangeSpecified ||
            (request.AutomaticPortRangeStart is >= MinimumPort and <= MaximumPort &&
             request.AutomaticPortRangeEnd is >= MinimumPort and <= MaximumPort &&
             request.AutomaticPortRangeStart <= request.AutomaticPortRangeEnd);
        return PersistencePortLease.IsSafeNodeId(request.NodeId) &&
            request.ServiceId != Guid.Empty &&
            request.TimeToLive > TimeSpan.Zero &&
            request.ExpectedVersion is null or >= 0 &&
            validPort && (!rangeSpecified || request.Port == 0) && validRange;
    }

    private static bool IsValidRenew(PersistencePortLeaseRenewRequest request) =>
        PersistencePortLease.IsSafeNodeId(request.NodeId) &&
        request.ServiceId != Guid.Empty &&
        request.Port is >= MinimumPort and <= MaximumPort &&
        request.LeaseVersion >= 0 &&
        request.TimeToLive > TimeSpan.Zero;

    private static bool IsValidRelease(PersistencePortLeaseReleaseRequest request) =>
        PersistencePortLease.IsSafeNodeId(request.NodeId) &&
        request.ServiceId != Guid.Empty &&
        request.Port is >= MinimumPort and <= MaximumPort &&
        request.LeaseVersion is null or >= 0;

    private static bool IsLeaseConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState is PostgresErrorCodes.UniqueViolation or
            PostgresErrorCodes.SerializationFailure or
            PostgresErrorCodes.DeadlockDetected;
}
