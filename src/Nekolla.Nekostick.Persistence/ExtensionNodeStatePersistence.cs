using System.Data;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence.Entities;
using DomainExtensionLoadState = Nekolla.Nekostick.Domain.ExtensionLoadState;
using PersistenceExtensionNodeState = Nekolla.Nekostick.Persistence.Entities.ExtensionNodeState;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Describes one node-local extension observation to persist.</summary>
public sealed record ExtensionNodeStateWrite(
    string ExtensionId,
    string? ObservedContentHash,
    ExtensionLoadState LoadState,
    string FailureCode = "None");

/// <summary>Persists node-local extension observations without changing global configuration.</summary>
public sealed class EfExtensionNodeStatePersistence
{
    private const int MaxAttempts = 3;
    private readonly NekostickDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>Creates a node-state persistence adapter.</summary>
    public EfExtensionNodeStatePersistence(NekostickDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Upserts the supplied observations and removes this node's rows outside the supplied record set.
    /// </summary>
    /// <param name="nodeId">The node that owns the observations.</param>
    /// <param name="states">The complete observation set for the published snapshot.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns><see langword="true" /> when the transaction committed.</returns>
    public async ValueTask<bool> UpsertAsync(
        string nodeId,
        IReadOnlyCollection<ExtensionNodeStateWrite> states,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeNodeId(nodeId) || states is null || !ValidateStates(states))
        {
            return false;
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return await UpsertOnceAsync(nodeId, states, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                attempt + 1 < MaxAttempts &&
                EfHostConfigRevisionHelper.IsTransactionConflict(exception))
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private async ValueTask<bool> UpsertOnceAsync(
        string nodeId,
        IReadOnlyCollection<ExtensionNodeStateWrite> states,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        var nodeExists = await _db.Nodes.AsNoTracking()
            .AnyAsync(value => value.NodeId == nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (!nodeExists)
        {
            return false;
        }

        var extensionIds = states
            .Select(static value => value.ExtensionId)
            .ToArray();
        var records = extensionIds.Length == 0
            ? new List<ExtensionRecord>()
            : await _db.ExtensionRecords
                .Where(value => extensionIds.Contains(value.ExtensionId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var recordIds = records.Select(static value => value.Id).ToHashSet();
        var recordsByExtensionId = records.ToDictionary(
            static value => value.ExtensionId,
            StringComparer.Ordinal);

        var existing = await _db.ExtensionNodeStates
            .Where(value => value.NodeId == nodeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        _db.ExtensionNodeStates.RemoveRange(existing.Where(value => !recordIds.Contains(value.ExtensionRecordId)));

        var now = _time.GetUtcNow().ToUniversalTime();
        foreach (var state in states)
        {
            if (!recordsByExtensionId.TryGetValue(state.ExtensionId, out var record))
            {
                continue;
            }

            var entity = existing.FirstOrDefault(value => value.ExtensionRecordId == record.Id);
            if (entity is null)
            {
                _db.ExtensionNodeStates.Add(new PersistenceExtensionNodeState
                {
                    NodeId = nodeId,
                    ExtensionRecordId = record.Id,
                    ObservedContentHash = state.ObservedContentHash,
                    LoadState = (DomainExtensionLoadState)state.LoadState,
                    FailureCode = state.FailureCode,
                    UpdatedAt = now
                });
            }
            else
            {
                entity.ObservedContentHash = state.ObservedContentHash;
                entity.LoadState = (DomainExtensionLoadState)state.LoadState;
                entity.FailureCode = state.FailureCode;
                entity.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool ValidateStates(IEnumerable<ExtensionNodeStateWrite> states)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (state is null ||
                !HostConfigurationSemanticValidator.IsSafeExtensionId(state.ExtensionId) ||
                !ids.Add(state.ExtensionId) ||
                !Enum.IsDefined(state.LoadState) ||
                !IsValidContentHash(state.ObservedContentHash) ||
                string.IsNullOrEmpty(state.FailureCode) ||
                state.FailureCode.Length > 64 ||
                state.FailureCode.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidContentHash(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        return value[7..].All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    private static bool IsSafeNodeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}

/// <summary>Atomically updates extension content digests together with configuration revisions.</summary>
public sealed class EfExtensionRecordContentPersistence
{
    private readonly NekostickDbContext _db;
    private readonly TimeProvider _time;
    private readonly EfHostConfigRevisionHelper _revisionHelper;

    /// <summary>Creates an extension content persistence adapter.</summary>
    public EfExtensionRecordContentPersistence(NekostickDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = timeProvider ?? TimeProvider.System;
        _revisionHelper = new EfHostConfigRevisionHelper(_db);
    }

    /// <summary>Changes an extension to the requested state and records its local content digest.</summary>
    public ValueTask<ConfigurationWriteResult> SetLoadStateAndContentHashAsync(
        string extensionId,
        long expectedRecordVersion,
        ExtensionLoadState state,
        string? contentHash,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(state) || !IsValidContentHash(contentHash) ||
            state != ExtensionLoadState.Loaded)
        {
            return ValueTask.FromResult(EfHostConfigRevisionHelper.ValidationWriteFailure());
        }

        return WriteAsync(
            extensionId,
            expectedRecordVersion,
            record =>
            {
                var currentState = (ExtensionLoadState)record.LoadState;
                if (!IsAllowedExtensionLoadStateTransition(currentState, state))
                {
                    return false;
                }

                record.LoadState = (DomainExtensionLoadState)state;
                record.ContentHash = contentHash;
                return true;
            },
            cancellationToken);
    }

    /// <summary>Changes an installed version and records the digest observed for that version.</summary>
    public ValueTask<ConfigurationWriteResult> UpdateInstalledVersionAndContentHashAsync(
        string extensionId,
        long expectedRecordVersion,
        string newVersion,
        string? contentHash,
        CancellationToken cancellationToken = default)
    {
        if (!HostConfigurationExtensionValidator.IsValidVersion(newVersion) ||
            !IsValidContentHash(contentHash))
        {
            return ValueTask.FromResult(EfHostConfigRevisionHelper.ValidationWriteFailure());
        }

        return WriteAsync(
            extensionId,
            expectedRecordVersion,
            record =>
            {
                record.InstalledVersion = newVersion;
                record.ContentHash = contentHash;
                return true;
            },
            cancellationToken);
    }

    /// <summary>Updates an extension digest without changing its load state or installed version.</summary>
    public ValueTask<ConfigurationWriteResult> SetContentHashAsync(
        string extensionId,
        long expectedRecordVersion,
        string? contentHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidContentHash(contentHash))
        {
            return ValueTask.FromResult(EfHostConfigRevisionHelper.ValidationWriteFailure());
        }

        return WriteAsync(
            extensionId,
            expectedRecordVersion,
            record =>
            {
                if (string.Equals(record.ContentHash, contentHash, StringComparison.Ordinal))
                {
                    return false;
                }

                record.ContentHash = contentHash;
                return true;
            },
            cancellationToken);
    }

    private async ValueTask<ConfigurationWriteResult> WriteAsync(
        string extensionId,
        long expectedRecordVersion,
        Func<ExtensionRecord, bool> mutate,
        CancellationToken cancellationToken)
    {
        if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId) || expectedRecordVersion < 0)
        {
            return EfHostConfigRevisionHelper.ValidationWriteFailure();
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _db.ExtensionRecords
                .SingleOrDefaultAsync(value => value.ExtensionId == extensionId, cancellationToken)
                .ConfigureAwait(false);
            var revision = await _db.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (record is null || revision is null)
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound));
            }

            if (!HostConfigurationSemanticValidator.IsUuidV7(record.Id) ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            if (record.Version != expectedRecordVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
            }

            if (!mutate(record))
            {
                return ConfigurationWriteResult.Success(revision.Version);
            }

            var now = _time.GetUtcNow().ToUniversalTime();
            record.UpdatedAt = now;
            record.Version = EfHostConfigRevisionHelper.IncrementVersion(record.Version);
            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion).ConfigureAwait(false);
            return ConfigurationWriteResult.Success(committedVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            return EfHostConfigRevisionHelper.ValidationWriteFailure();
        }
        catch (DbUpdateConcurrencyException)
        {
            return EfHostConfigRevisionHelper.ConflictWriteFailure();
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            return EfHostConfigRevisionHelper.ConflictWriteFailure();
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            return EfHostConfigRevisionHelper.ConflictWriteFailure();
        }
        catch (InvalidOperationException)
        {
            return EfHostConfigRevisionHelper.ValidationWriteFailure();
        }
        catch (DbUpdateException)
        {
            return EfHostConfigRevisionHelper.StorageWriteFailure();
        }
        catch (Exception)
        {
            return EfHostConfigRevisionHelper.StorageWriteFailure();
        }
    }

    private static bool IsAllowedExtensionLoadStateTransition(
        ExtensionLoadState currentState,
        ExtensionLoadState desiredState) =>
        desiredState switch
        {
            ExtensionLoadState.Disabled => true,
            ExtensionLoadState.Loaded => currentState is
                ExtensionLoadState.Disabled or
                ExtensionLoadState.Stopped or
                ExtensionLoadState.Failed,
            _ => false
        };

    private static bool IsValidContentHash(string? value)
    {
        if (value is null)
        {
            return true;
        }

        return value.Length == 71 &&
            value.StartsWith("sha256:", StringComparison.Ordinal) &&
            value[7..].All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }
}
