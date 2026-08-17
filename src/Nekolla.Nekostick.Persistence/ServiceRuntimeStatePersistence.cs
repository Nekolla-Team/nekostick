using System.Data;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Identifies the fixed result of a service runtime write.</summary>
public enum ServiceRuntimePersistenceStatus
{
    /// <summary>The runtime state was committed.</summary>
    Applied,

    /// <summary>The optimistic version did not match.</summary>
    Conflict,

    /// <summary>The node or service was not found.</summary>
    NotFound,

    /// <summary>The database was unavailable.</summary>
    DatabaseUnavailable,

    /// <summary>The write was cancelled.</summary>
    Cancelled,

    /// <summary>The request was rejected by safe validation.</summary>
    Rejected
}

/// <summary>Contains safe, immutable persisted runtime state.</summary>
public sealed record PersistenceServiceRuntimeSnapshot(
    Guid ServiceId,
    string NodeId,
    ServiceRuntimeState State,
    long Version,
    DateTimeOffset UpdatedAt);

/// <summary>Describes a node-owned service runtime write.</summary>
public sealed record ServiceRuntimeWriteRequest(
    string NodeId,
    Guid ServiceId,
    ServiceRuntimeState State,
    long? ExpectedVersion = null);

/// <summary>Contains a fixed runtime persistence result.</summary>
public sealed record ServiceRuntimePersistenceResult(
    ServiceRuntimePersistenceStatus Status,
    PersistenceServiceRuntimeSnapshot? Snapshot = null);

/// <summary>Defines durable runtime reads and optimistic writes.</summary>
public interface IServiceRuntimePersistence
{
    /// <summary>Reads runtime state for one service owned by one node.</summary>
    ValueTask<PersistenceServiceRuntimeSnapshot?> ReadRuntimeAsync(
        Guid serviceId,
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>Writes runtime state with an optional optimistic version check.</summary>
    ValueTask<ServiceRuntimePersistenceResult> WriteRuntimeAsync(
        ServiceRuntimeWriteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed partial class EfServiceRuntimePersistence
{
    /// <inheritdoc />
    public async ValueTask<PersistenceServiceRuntimeSnapshot?> ReadRuntimeAsync(
        Guid serviceId,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (serviceId == Guid.Empty || !IsSafeNodeId(nodeId)) return null;
        try
        {
            var value = await _db.ServiceRuntimes.AsNoTracking().SingleOrDefaultAsync(
                item => item.ServiceId == serviceId && item.NodeId == nodeId,
                cancellationToken).ConfigureAwait(false);
            return value is null ? null : ToRuntimeSnapshot(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <inheritdoc />
    public async ValueTask<ServiceRuntimePersistenceResult> WriteRuntimeAsync(
        ServiceRuntimeWriteRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.ServiceId == Guid.Empty || !IsSafeNodeId(request.NodeId) ||
            !Enum.IsDefined(request.State.Lifecycle) || !Enum.IsDefined(request.State.Health) ||
            request.State.RestartCount < 0 || request.ExpectedVersion is < 0)
        {
            return new(ServiceRuntimePersistenceStatus.Rejected);
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var nodeExists = await _db.Nodes.AsNoTracking().AnyAsync(
                value => value.NodeId == request.NodeId,
                cancellationToken).ConfigureAwait(false);
            var serviceExists = await _db.Services.AsNoTracking().AnyAsync(
                value => value.Id == request.ServiceId,
                cancellationToken).ConfigureAwait(false);
            if (!nodeExists || !serviceExists)
            {
                return new(ServiceRuntimePersistenceStatus.NotFound);
            }

            var value = await _db.ServiceRuntimes.SingleOrDefaultAsync(
                item => item.ServiceId == request.ServiceId && item.NodeId == request.NodeId,
                cancellationToken).ConfigureAwait(false);
            if (value is not null && request.ExpectedVersion is not null &&
                value.Version != request.ExpectedVersion.Value)
            {
                return new(ServiceRuntimePersistenceStatus.Conflict);
            }

            if (value is null && request.ExpectedVersion is not null)
            {
                return new(ServiceRuntimePersistenceStatus.Conflict);
            }

            var now = _time.GetUtcNow().ToUniversalTime();
            if (value is null)
            {
                value = new ServiceRuntime
                {
                    ServiceId = request.ServiceId,
                    NodeId = request.NodeId,
                    CreatedAt = now,
                    Version = 1
                };
                _db.ServiceRuntimes.Add(value);
            }
            else
            {
                if (value.Version == long.MaxValue) return new(ServiceRuntimePersistenceStatus.Rejected);
                value.Version++;
            }

            value.Lifecycle = request.State.Lifecycle;
            value.Health = request.State.Health;
            value.RestartCount = request.State.RestartCount;
            value.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ServiceRuntimePersistenceStatus.Applied, ToRuntimeSnapshot(value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ServiceRuntimePersistenceStatus.Cancelled);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(ServiceRuntimePersistenceStatus.Conflict);
        }
        catch (Exception)
        {
            return new(ServiceRuntimePersistenceStatus.DatabaseUnavailable);
        }
    }

    private static PersistenceServiceRuntimeSnapshot ToRuntimeSnapshot(ServiceRuntime value) =>
        new(
            value.ServiceId,
            value.NodeId,
            new ServiceRuntimeState(value.Lifecycle, value.Health, value.RestartCount),
            value.Version,
            value.UpdatedAt);

    private static bool IsSafeNodeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}
