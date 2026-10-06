using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Implements the transactional PostgreSQL lease boundary with safe outcomes.</summary>
public sealed class EfPortLeaseStore : IPersistencePortLeaseStore, IAsyncDisposable
{
    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;
    private const int MaximumTransientAttempts = 3;
    private readonly NekostickDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the lease store.</summary>
    /// <param name="db">The persistence context.</param>
    /// <param name="timeProvider">The clock used for persisted timestamps.</param>
    /// <param name="logger">The optional persistence logger.</param>
    public EfPortLeaseStore(
        NekostickDbContext db,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
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

        return await ExecuteWithRetryAsync(
            "Acquire",
            request.NodeId,
            request.ServiceId,
            request.Port,
            request,
            static (store, value, token) => store.AcquireAttemptAsync(value, token),
            MutationFailureResult,
            cancellationToken).ConfigureAwait(false);
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

        return await ExecuteWithRetryAsync(
            "Renew",
            request.NodeId,
            request.ServiceId,
            request.Port,
            request,
            static (store, value, token) => store.RenewAttemptAsync(value, token),
            MutationFailureResult,
            cancellationToken).ConfigureAwait(false);
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

        return await ExecuteWithRetryAsync(
            "Release",
            request.NodeId,
            request.ServiceId,
            request.Port,
            request,
            static (store, value, token) => store.ReleaseAttemptAsync(value, token),
            MutationFailureResult,
            cancellationToken).ConfigureAwait(false);
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

        return await ExecuteWithRetryAsync(
            "ReadActive",
            nodeId,
            Guid.Empty,
            0,
            nodeId,
            static (store, value, token) => store.ReadActiveAttemptAsync(value, token),
            SnapshotFailureResult,
            cancellationToken).ConfigureAwait(false);
    }

    private enum LeaseOperationFailure
    {
        Conflict,
        RetryableTransient,
        Cancelled,
        DatabaseUnavailable
    }

    private static readonly Func<LeaseOperationFailure, PersistencePortLeaseOperationResult> MutationFailureResult =
        CreateMutationFailureResult;
    private static readonly Func<LeaseOperationFailure, PersistencePortLeaseSnapshotResult> SnapshotFailureResult =
        CreateSnapshotFailureResult;

    private async ValueTask<TResult> ExecuteWithRetryAsync<TRequest, TResult>(
        string operation,
        string nodeId,
        Guid serviceId,
        int port,
        TRequest request,
        Func<EfPortLeaseStore, TRequest, CancellationToken, Task<TResult>> executeAttempt,
        Func<LeaseOperationFailure, TResult> createFailureResult,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DetachTrackedPortLeases();
                try
                {
                    return await executeAttempt(this, request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRetryableTransient(exception))
                {
                    DetachTrackedPortLeases();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (attempt < MaximumTransientAttempts)
                    {
                        continue;
                    }

                    PersistenceLogMessages.PortLeaseFailed(
                        _logger,
                        exception,
                        operation,
                        nodeId,
                        serviceId,
                        port);
                    return createFailureResult(LeaseOperationFailure.RetryableTransient);
                }
                catch (DbUpdateConcurrencyException exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DetachTrackedPortLeases();
                    PersistenceLogMessages.PortLeaseFailed(
                        _logger,
                        exception,
                        operation,
                        nodeId,
                        serviceId,
                        port);
                    return createFailureResult(LeaseOperationFailure.Conflict);
                }
                catch (Exception exception) when (IsUniqueViolation(exception))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DetachTrackedPortLeases();
                    PersistenceLogMessages.PortLeaseFailed(
                        _logger,
                        exception,
                        operation,
                        nodeId,
                        serviceId,
                        port);
                    return createFailureResult(LeaseOperationFailure.Conflict);
                }
                catch (Exception exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DetachTrackedPortLeases();
                    PersistenceLogMessages.PortLeaseFailed(
                        _logger,
                        exception,
                        operation,
                        nodeId,
                        serviceId,
                        port);
                    return createFailureResult(LeaseOperationFailure.DatabaseUnavailable);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (entered)
            {
                DetachTrackedPortLeases();
            }

            PersistenceLogMessages.PortLeaseCancelled(_logger, operation, nodeId, serviceId, port);
            return createFailureResult(LeaseOperationFailure.Cancelled);
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.PortLeaseFailed(
                _logger,
                exception,
                operation,
                nodeId,
                serviceId,
                port);
            return createFailureResult(LeaseOperationFailure.DatabaseUnavailable);
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private async Task<PersistencePortLeaseOperationResult> AcquireAttemptAsync(
        PersistencePortLeaseAcquireRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();
        var persistedNow = TruncateToMicrosecond(now);
        if (!await HasUsableOwnerAsync(
                request.NodeId,
                request.ServiceId,
                requireEnabledService: true,
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new(PersistencePortLeaseOperationStatus.Rejected);
        }

        var existingForGeneration = await _db.PortLeases
            .SingleOrDefaultAsync(
                value => value.NodeId == request.NodeId &&
                    value.ServiceId == request.ServiceId &&
                    value.GenerationId == request.GenerationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingForGeneration is not null && existingForGeneration.LeaseExpiresAt > now)
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }

        if (existingForGeneration is not null && request.ExpectedVersion is not null &&
            existingForGeneration.Version != request.ExpectedVersion.Value)
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }

        if (existingForGeneration is null && request.ExpectedVersion is not null)
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
            GenerationId = request.GenerationId,
            LeaseExpiresAt = leaseExpiresAt.Value,
            RenewedAt = persistedNow,
            Version = 1,
            CreatedAt = persistedNow,
            UpdatedAt = persistedNow
        };
        _db.PortLeases.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(ToSnapshot(entity));
    }

    private async Task<PersistencePortLeaseOperationResult> RenewAttemptAsync(
        PersistencePortLeaseRenewRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();
        var persistedNow = TruncateToMicrosecond(now);
        if (!await HasUsableOwnerAsync(
                request.NodeId,
                request.ServiceId,
                requireEnabledService: true,
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new(PersistencePortLeaseOperationStatus.Rejected);
        }

        if (await ReclaimExpiredAsync(request.NodeId, now, cancellationToken).ConfigureAwait(false))
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var entity = await _db.PortLeases.SingleOrDefaultAsync(
            value => value.NodeId == request.NodeId &&
                value.ServiceId == request.ServiceId &&
                value.GenerationId == request.GenerationId &&
                value.Port == request.Port,
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
        entity.RenewedAt = persistedNow;
        entity.Version++;
        entity.UpdatedAt = persistedNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(ToSnapshot(entity));
    }

    private async Task<PersistencePortLeaseOperationResult> ReleaseAttemptAsync(
        PersistencePortLeaseReleaseRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();
        if (!await HasUsableOwnerAsync(
                request.NodeId,
                request.ServiceId,
                requireEnabledService: false,
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new(PersistencePortLeaseOperationStatus.Rejected);
        }

        if (await ReclaimExpiredAsync(request.NodeId, now, cancellationToken).ConfigureAwait(false))
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var entity = await _db.PortLeases.SingleOrDefaultAsync(
            value => value.NodeId == request.NodeId &&
                value.ServiceId == request.ServiceId &&
                value.GenerationId == request.GenerationId &&
                value.Port == request.Port,
            cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return new(PersistencePortLeaseOperationStatus.NotFound);
        }

        if (entity.Version != request.LeaseVersion)
        {
            return new(PersistencePortLeaseOperationStatus.Conflict);
        }

        var snapshot = ToSnapshot(entity);
        _db.PortLeases.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(snapshot);
    }

    private async Task<PersistencePortLeaseSnapshotResult> ReadActiveAttemptAsync(
        string nodeId,
        CancellationToken cancellationToken)
    {
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
            .OrderBy(value => value.ServiceId).ThenBy(value => value.GenerationId).ThenBy(value => value.Port)
            .Select(value => new PersistencePortLease(
                value.NodeId,
                value.ServiceId,
                value.GenerationId,
                value.Port,
                value.CreatedAt,
                value.LeaseExpiresAt,
                value.Version))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(PersistencePortLeaseSnapshotStatus.Available, leases.ToImmutableArray());
    }

    private void DetachTrackedPortLeases()
    {
        foreach (var entry in _db.ChangeTracker.Entries<PortLease>().ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static PersistencePortLeaseOperationResult CreateMutationFailureResult(
        LeaseOperationFailure failure) =>
        failure switch
        {
            LeaseOperationFailure.Conflict => new(PersistencePortLeaseOperationStatus.Conflict),
            LeaseOperationFailure.RetryableTransient => new(PersistencePortLeaseOperationStatus.RetryableTransient),
            LeaseOperationFailure.Cancelled => PersistencePortLeaseOperationResult.Cancelled(),
            _ => PersistencePortLeaseOperationResult.Unavailable()
        };

    private static PersistencePortLeaseSnapshotResult CreateSnapshotFailureResult(
        LeaseOperationFailure failure) =>
        new(failure switch
        {
            LeaseOperationFailure.Cancelled => PersistencePortLeaseSnapshotStatus.Cancelled,
            LeaseOperationFailure.RetryableTransient => PersistencePortLeaseSnapshotStatus.RetryableTransient,
            _ => PersistencePortLeaseSnapshotStatus.DatabaseUnavailable
        });

    private static bool IsRetryableTransient(Exception exception) =>
        FindPostgresException(exception)?.SqlState is
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private static bool IsUniqueViolation(Exception exception) =>
        FindPostgresException(exception)?.SqlState == PostgresErrorCodes.UniqueViolation;

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }

    private async Task<bool> HasUsableOwnerAsync(
        string nodeId,
        Guid serviceId,
        bool requireEnabledService,
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
            value => value.Id == serviceId && (!requireEnabledService || value.Enabled),
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
        new(value.NodeId, value.ServiceId, value.GenerationId, value.Port, value.CreatedAt, value.LeaseExpiresAt, value.Version);

    private static DateTimeOffset TruncateToMicrosecond(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private DateTimeOffset? TryGetExpiry(DateTimeOffset now, TimeSpan ttl)
    {
        try
        {
            return TruncateToMicrosecond(now + ttl);
        }
        catch (ArgumentOutOfRangeException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "CalculateLeaseExpiry", "global");
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
            UuidV7.IsVersion7(request.GenerationId) &&
            request.TimeToLive > TimeSpan.Zero &&
            request.ExpectedVersion is null or >= 0 &&
            validPort && (!rangeSpecified || request.Port == 0) && validRange;
    }

    private static bool IsValidRenew(PersistencePortLeaseRenewRequest request) =>
        PersistencePortLease.IsSafeNodeId(request.NodeId) &&
        request.ServiceId != Guid.Empty &&
        UuidV7.IsVersion7(request.GenerationId) &&
        request.Port is >= MinimumPort and <= MaximumPort &&
        request.LeaseVersion >= 0 &&
        request.TimeToLive > TimeSpan.Zero;

    private static bool IsValidRelease(PersistencePortLeaseReleaseRequest request) =>
        PersistencePortLease.IsSafeNodeId(request.NodeId) &&
        request.ServiceId != Guid.Empty &&
        UuidV7.IsVersion7(request.GenerationId) &&
        request.Port is >= MinimumPort and <= MaximumPort &&
        request.LeaseVersion >= 0;

}
