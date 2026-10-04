using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Provides the transactional, PostgreSQL-backed host configuration boundary.</summary>
public sealed class EfHostConfigApi : IHostConfigApi, IAsyncDisposable
{
    private readonly NekostickDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EfHostConfigEntityOperations _entityOperations;
    private readonly EfHostConfigRevisionHelper _revisionHelper;
    private readonly AsyncLocal<OwnerWriteContext?> _ownerWriteContext = new();

    private sealed record OwnerWriteContext(
        string ExtensionId,
        IReadOnlySet<Guid> RouteIds,
        IReadOnlySet<Guid> ServiceIds);


    /// <summary>Creates the EF-backed host configuration API.</summary>
    /// <param name="dbContext">The scoped PostgreSQL context owned by the host.</param>
    /// <param name="timeProvider">The clock used for persisted timestamps.</param>
    /// <param name="logger">The optional persistence logger.</param>
    public EfHostConfigApi(NekostickDbContext dbContext, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _entityOperations = new EfHostConfigEntityOperations(_dbContext);
        _revisionHelper = new EfHostConfigRevisionHelper(_dbContext, _logger);
    }

    /// <inheritdoc />
    public HostApiVersion ApiVersion => HostApiVersion.Current;

    /// <summary>Releases the API's operation gate.</summary>
    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var revision = await _dbContext.ConfigurationRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            var globalSettings = await _dbContext.GlobalSettings
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (revision is null || globalSettings is null ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId) ||
                globalSettings.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId))
            {
                return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' or GlobalSettings '{PersistenceDatabaseDefaults.SeedGlobalSettingsId}' is missing or has an unexpected identity."));
            }

            var routes = await _dbContext.Routes
                .AsNoTracking()
                .OrderBy(value => value.Id)
                .ToListAsync(cancellationToken);
            var services = await _dbContext.Services
                .AsNoTracking()
                .OrderBy(value => value.Id)
                .ToListAsync(cancellationToken);
            var extensionRecords = await _dbContext.ExtensionRecords
                .AsNoTracking()
                .OrderBy(value => value.ExtensionId)
                .ThenBy(value => value.Id)
                .ToListAsync(cancellationToken);
            var extensionSettings = await _dbContext.ExtensionSettings
                .AsNoTracking()
                .OrderBy(value => value.ExtensionRecordId)
                .ThenBy(value => value.Id)
                .ToListAsync(cancellationToken);

            var snapshot = EfHostConfigDtoEntityMapper.MapSnapshot(
                revision,
                globalSettings,
                routes,
                services,
                extensionRecords,
                extensionSettings);
            if (!HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot, out var validationMessage, _logger))
            {
                return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.Validation,
                        validationMessage ?? "ReadSnapshot found invalid global settings, routes, services, extension records, extension settings, or persisted entity revisions."));
            }

            return ConfigurationReadResult<HostConfigurationSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "ReadSnapshot", "global");
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "ReadSnapshot", "global");
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Validation,
                    "ReadSnapshot found a persisted configuration revision or value that violates its version or schema rules."));
        }
        catch (ArgumentException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "ReadSnapshot", "global");
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Validation,
                    "ReadSnapshot could not map the persisted global configuration because a stored value violates the contract."));
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "ReadSnapshot", "global");
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.StorageUnavailable,
                    "ReadSnapshot could not read ConfigurationRevision, GlobalSettings, Route, Service, ExtensionRecord, or ExtensionSetting data from the persistence store."));
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "ReadSnapshot", "global");
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.StorageUnavailable,
                    "ReadSnapshot could not read ConfigurationRevision, GlobalSettings, Route, Service, ExtensionRecord, or ExtensionSetting data from the persistence store."));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> WriteSnapshotAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedVersion < 0 || changes is null)
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteSnapshot requires a non-negative expected revision and a non-null configuration change set.");
            }

            if (!HostConfigurationSemanticValidator.TryValidateChangeSet(changes, out var validationMessage, _logger))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    validationMessage ?? "WriteSnapshot rejected invalid global settings, routes, services, extension records, or extension settings in the change set.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var revision = await _dbContext.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            var globalSettings = await _dbContext.GlobalSettings.SingleOrDefaultAsync(cancellationToken);
            if (revision is null || globalSettings is null)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' or GlobalSettings '{PersistenceDatabaseDefaults.SeedGlobalSettingsId}' was not found."));
            }

            if (revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId) ||
                globalSettings.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteSnapshot found an unexpected identity for the singleton ConfigurationRevision or GlobalSettings record.");
            }

            if (revision.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"WriteSnapshot expected global revision {expectedVersion}, but the actual revision is {revision.Version}.");
            }

            if (changes.GlobalSettings.Version != globalSettings.Version)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"WriteSnapshot expected GlobalSettings revision {changes.GlobalSettings.Version}, but the actual revision is {globalSettings.Version}.");
            }

            var routes = await _dbContext.Routes.ToListAsync(cancellationToken);
            var services = await _dbContext.Services.ToListAsync(cancellationToken);
            var extensionRecords = await _dbContext.ExtensionRecords.ToListAsync(cancellationToken);
            var extensionNodeStates = await _dbContext.ExtensionNodeStates.ToListAsync(cancellationToken);
            var extensionSettings = await _dbContext.ExtensionSettings.ToListAsync(cancellationToken);

            if (!EfHostConfigEntityOperations.TryValidateReplacementVersions(
                    changes,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    globalSettings,
                    out var versionsAreValid,
                    out var versionConflictMessage))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteSnapshot could not validate the submitted route, service, extension record, or settings revisions.");
            }

            if (!versionsAreValid)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    versionConflictMessage ?? "WriteSnapshot found a configuration entity revision that differs from the stored revision.");
            }

            var removedServiceIds = services
                .Select(value => value.Id)
                .Except(changes.Services.Select(value => value.Id))
                .ToArray();
            if (removedServiceIds.Length != 0 && await _dbContext.PortLeases
                    .AsNoTracking()
                    .AnyAsync(value => removedServiceIds.Contains(value.ServiceId), cancellationToken))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteSnapshot cannot remove a Service while a persisted PortLease references a service being removed.");
            }

            var now = _timeProvider.GetUtcNow();
            bool changed;
            if (_ownerWriteContext.Value is { } ownerContext)
            {
                changed = _entityOperations.ApplyReplacement(
                    changes,
                    revision,
                    globalSettings,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    extensionNodeStates,
                    now,
                    ownerContext.ExtensionId,
                    ownerContext.RouteIds,
                    ownerContext.ServiceIds);
            }
            else
            {
                changed = _entityOperations.ApplyReplacement(
                    changes,
                    revision,
                    globalSettings,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    extensionNodeStates,
                    now);
            }

            if (!changed)
            {
                await transaction.CommitAsync(cancellationToken);
                return ConfigurationWriteResult.Success(revision.Version);
            }

            var newVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = newVersion;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(newVersion);
            return ConfigurationWriteResult.Success(newVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "WriteSnapshot", "global");
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "WriteSnapshot could not apply the configuration because an entity revision or identity exceeded the supported range.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"WriteSnapshot expected global revision {expectedVersion}, but an actual persisted revision or entity version changed before commit.");
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"WriteSnapshot expected global revision {expectedVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict while persisting configuration entities.");
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"WriteSnapshot expected global revision {expectedVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict while persisting configuration entities.");
        }
        catch (InvalidOperationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "WriteSnapshot could not apply the change set because persisted configuration entity keys or relationships are inconsistent.");
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "WriteSnapshot could not persist ConfigurationRevision, GlobalSettings, Route, Service, ExtensionRecord, ExtensionNodeState, or ExtensionSetting data because the database was unavailable or rejected a primary-key, foreign-key, unique, or check constraint.");
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "WriteSnapshot", "global");
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "WriteSnapshot could not persist ConfigurationRevision, GlobalSettings, Route, Service, ExtensionRecord, ExtensionNodeState, or ExtensionSetting data because the database was unavailable or rejected a primary-key, foreign-key, unique, or check constraint.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically persists extension records using <see cref="ExtensionLoadState.Loaded"/> as the compatibility default.
    /// </summary>
    /// <param name="expectedVersion">The global revision observed during manifest discovery.</param>
    /// <param name="records">The validated records to create.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed global revision or a safe error.</returns>
    public ValueTask<ConfigurationWriteResult> PersistDiscoveredExtensionRecordsAsync(
        long expectedVersion,
        ImmutableArray<ExtensionRecordConfiguration> records,
        CancellationToken cancellationToken = default) =>
        PersistDiscoveredExtensionRecordsAsync(
            ExtensionLoadState.Loaded,
            expectedVersion,
            records,
            cancellationToken);

    /// <summary>
    /// Atomically persists extension records that are absent from the durable store without creating
    /// extension settings. Existing records are authoritative and an exact repeat is a no-op.
    /// </summary>
    /// <param name="initialState">The initial state required for every record being persisted.</param>
    /// <param name="expectedVersion">The global revision observed during manifest discovery.</param>
    /// <param name="records">The validated records to create.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed global revision or a safe error.</returns>
    public async ValueTask<ConfigurationWriteResult> PersistDiscoveredExtensionRecordsAsync(
        ExtensionLoadState initialState,
        long expectedVersion,
        ImmutableArray<ExtensionRecordConfiguration> records,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (initialState is not (ExtensionLoadState.Loaded or ExtensionLoadState.Disabled) ||
                expectedVersion < 0 || records.IsDefaultOrEmpty ||
                records.Any(record => record is null || record.LoadState != initialState) ||
                !HostConfigurationSemanticValidator.TryValidateExtensionRecords(records, _logger))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "PersistDiscoveredExtensionRecords requires a valid initial state, non-negative expected revision, and non-empty valid extension records.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var revision = await _dbContext.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            var globalSettings = await _dbContext.GlobalSettings
                .SingleOrDefaultAsync(cancellationToken);
            if (revision is null || globalSettings is null)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' or GlobalSettings '{PersistenceDatabaseDefaults.SeedGlobalSettingsId}' was not found."));
            }

            if (revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId) ||
                globalSettings.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "PersistDiscoveredExtensionRecords found an unexpected identity for the singleton ConfigurationRevision or GlobalSettings record.");
            }

            // The revision is checked before the idempotence path so a stale caller cannot
            // mistake a concurrent commit for a successful no-op.
            if (revision.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"PersistDiscoveredExtensionRecords expected global revision {expectedVersion}, but the actual revision is {revision.Version}.");
            }

            var extensionIds = records
                .Select(static record => record.ExtensionId)
                .ToArray();
            var existing = await _dbContext.ExtensionRecords
                .Where(record => extensionIds.Contains(record.ExtensionId))
                .ToListAsync(cancellationToken);
            var existingById = existing.ToDictionary(
                static record => record.ExtensionId,
                StringComparer.Ordinal);
            foreach (var record in records)
            {
                if (!existingById.TryGetValue(record.ExtensionId, out var existingRecord))
                {
                    continue;
                }

                // An existing row wins even when its record version or timestamps differ.
                // A different installed version, content hash, or state is a real cross-process conflict.
                if (!string.Equals(existingRecord.InstalledVersion, record.Version, StringComparison.Ordinal) ||
                    !string.Equals(existingRecord.ContentHash, record.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                    (Nekolla.Nekostick.Contracts.ExtensionLoadState)existingRecord.LoadState != initialState)
                {
                    return EfHostConfigRevisionHelper.ConflictWriteFailure(
                        $"PersistDiscoveredExtensionRecords expected extension '{record.ExtensionId}' at installed version '{record.Version}' and state '{initialState}', but the actual stored ExtensionRecord does not match those values.");
                }
            }

            var missing = records
                .Where(record => !existingById.ContainsKey(record.ExtensionId))
                .ToArray();
            if (missing.Length == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return ConfigurationWriteResult.Success(revision.Version);
            }

            var now = _timeProvider.GetUtcNow();
            _dbContext.ExtensionRecords.AddRange(missing.Select(record => new ExtensionRecord
            {
                Id = EfHostConfigRevisionHelper.NewUuidV7(),
                ExtensionId = record.ExtensionId,
                InstalledVersion = record.Version,
                ContentHash = record.ContentHash,
                LoadState = (Nekolla.Nekostick.Domain.ExtensionLoadState)initialState,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            }));

            var newVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = newVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = HostConfigurationWriteContext.CurrentCommittedBy;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(newVersion);
            return ConfigurationWriteResult.Success(newVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "PersistDiscoveredExtensionRecords", "global");
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "PersistDiscoveredExtensionRecords could not advance the global ConfigurationRevision while saving the ExtensionRecord set.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"PersistDiscoveredExtensionRecords expected global revision {expectedVersion}, but an actual revision or ExtensionRecord row changed before commit.");
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"PersistDiscoveredExtensionRecords expected global revision {expectedVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict while inserting ExtensionRecord rows.");
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"PersistDiscoveredExtensionRecords expected global revision {expectedVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict while inserting ExtensionRecord rows.");
        }
        catch (InvalidOperationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "PersistDiscoveredExtensionRecords found duplicate or inconsistent ExtensionRecord identifiers or load states.");
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "PersistDiscoveredExtensionRecords could not insert ExtensionRecord rows or update ConfigurationRevision because the database was unavailable or rejected a primary-key, unique, or check constraint.");
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "PersistDiscoveredExtensionRecords", "global");
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "PersistDiscoveredExtensionRecords could not insert ExtensionRecord rows or update ConfigurationRevision because the database was unavailable or rejected a primary-key, unique, or check constraint.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically changes one extension record's persisted load state.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="expectedRecordVersion">The expected extension record version.</param>
    /// <param name="state">The requested persisted load state.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed global revision or a safe error.</returns>
    public async ValueTask<ConfigurationWriteResult> SetExtensionLoadStateAsync(
        string extensionId,
        long expectedRecordVersion,
        ExtensionLoadState state,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId) ||
                expectedRecordVersion < 0 || !Enum.IsDefined(state))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "SetExtensionLoadState requires a safe extension ID, non-negative expected record revision, and a defined load state.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
            var record = await _dbContext.ExtensionRecords
                .SingleOrDefaultAsync(value => value.ExtensionId == extensionId, cancellationToken);
            var revision = await _dbContext.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            if (record is null || revision is null)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ExtensionRecord '{extensionId}' or ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' was not found."));
            }

            if (!HostConfigurationSemanticValidator.IsUuidV7(record.Id) ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "SetExtensionLoadState found an invalid ExtensionRecord identity or ConfigurationRevision identity.");
            }

            if (record.Version != expectedRecordVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"SetExtensionLoadState expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision is {record.Version}.");
            }

            var currentState = (ExtensionLoadState)record.LoadState;
            if (!Enum.IsDefined(currentState))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "SetExtensionLoadState found a persisted ExtensionRecord with an undefined load state.");
            }

            if (currentState == state)
            {
                await transaction.CommitAsync(cancellationToken);
                return ConfigurationWriteResult.Success(revision.Version);
            }

            if (!IsAllowedExtensionLoadStateTransition(currentState, state))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    $"SetExtensionLoadState cannot transition extension '{extensionId}' from {currentState} to {state}.");
            }

            var now = _timeProvider.GetUtcNow();
            record.LoadState = (Nekolla.Nekostick.Domain.ExtensionLoadState)state;
            record.UpdatedAt = now;
            record.Version = EfHostConfigRevisionHelper.IncrementVersion(record.Version);

            var newVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = newVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = HostConfigurationWriteContext.CurrentCommittedBy;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(newVersion);
            return ConfigurationWriteResult.Success(newVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "SetExtensionLoadState", extensionId);
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "SetExtensionLoadState could not advance the ExtensionRecord or global ConfigurationRevision beyond its supported revision range.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"SetExtensionLoadState expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision changed during commit.");
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"SetExtensionLoadState expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict.");
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"SetExtensionLoadState expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict.");
        }
        catch (InvalidOperationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "SetExtensionLoadState could not update ExtensionRecord because its persisted identity or state is inconsistent.");
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "SetExtensionLoadState could not update ExtensionRecord or ConfigurationRevision because the database was unavailable or rejected the ExtensionRecord load-state check constraint.");
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "SetExtensionLoadState", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "SetExtensionLoadState could not update ExtensionRecord or ConfigurationRevision because the database was unavailable or rejected the ExtensionRecord load-state check constraint.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically updates one extension record's installed version.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="expectedRecordVersion">The expected extension record version.</param>
    /// <param name="newVersion">The validated semantic version text.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed global revision or a safe error.</returns>
    public async ValueTask<ConfigurationWriteResult> UpdateExtensionInstalledVersionAsync(
        string extensionId,
        long expectedRecordVersion,
        string newVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId) ||
                expectedRecordVersion < 0 || !HostConfigurationExtensionValidator.IsValidVersion(newVersion))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "UpdateExtensionInstalledVersion requires a safe extension ID, non-negative expected record revision, and valid semantic version.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
            var record = await _dbContext.ExtensionRecords
                .SingleOrDefaultAsync(value => value.ExtensionId == extensionId, cancellationToken);
            var revision = await _dbContext.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            if (record is null || revision is null)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ExtensionRecord '{extensionId}' or ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' was not found."));
            }

            if (!HostConfigurationSemanticValidator.IsUuidV7(record.Id) ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "UpdateExtensionInstalledVersion found an invalid ExtensionRecord identity or ConfigurationRevision identity.");
            }

            if (record.Version != expectedRecordVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"UpdateExtensionInstalledVersion expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision is {record.Version}.");
            }

            if (string.Equals(record.InstalledVersion, newVersion, StringComparison.Ordinal) &&
                record.ContentHash is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ConfigurationWriteResult.Success(revision.Version);
            }

            var now = _timeProvider.GetUtcNow();
            record.InstalledVersion = newVersion;
            record.ContentHash = null;
            record.UpdatedAt = now;
            record.Version = EfHostConfigRevisionHelper.IncrementVersion(record.Version);

            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = HostConfigurationWriteContext.CurrentCommittedBy;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion);
            return ConfigurationWriteResult.Success(committedVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "UpdateExtensionInstalledVersion", extensionId);
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "UpdateExtensionInstalledVersion could not advance the ExtensionRecord or global ConfigurationRevision beyond its supported revision range.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"UpdateExtensionInstalledVersion expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision changed during commit.");
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"UpdateExtensionInstalledVersion expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict.");
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"UpdateExtensionInstalledVersion expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict.");
        }
        catch (InvalidOperationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "UpdateExtensionInstalledVersion found an inconsistent ExtensionRecord identity or revision.");
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "UpdateExtensionInstalledVersion could not update ExtensionRecord or ConfigurationRevision because the database was unavailable or rejected an ExtensionRecord text check constraint.");
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "UpdateExtensionInstalledVersion", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "UpdateExtensionInstalledVersion could not update ExtensionRecord or ConfigurationRevision because the database was unavailable or rejected an ExtensionRecord text check constraint.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically deletes one absent extension record and its owned configuration.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="expectedRecordVersion">The expected extension record version.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed global revision or a safe error.</returns>
    public async ValueTask<ConfigurationWriteResult> DeleteExtensionRecordCascadeAsync(
        string extensionId,
        long expectedRecordVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId) || expectedRecordVersion < 0)
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "DeleteExtensionRecordCascade requires a safe extension ID and non-negative expected record revision.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
            var record = await _dbContext.ExtensionRecords
                .SingleOrDefaultAsync(value => value.ExtensionId == extensionId, cancellationToken);
            var revision = await _dbContext.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            if (record is null || revision is null)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ExtensionRecord '{extensionId}' or ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' was not found."));
            }

            if (!HostConfigurationSemanticValidator.IsUuidV7(record.Id) ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "DeleteExtensionRecordCascade found an invalid ExtensionRecord identity or ConfigurationRevision identity.");
            }

            if (record.Version != expectedRecordVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"DeleteExtensionRecordCascade expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision is {record.Version}.");
            }

            var settings = await _dbContext.ExtensionSettings
                .Where(value => value.ExtensionRecordId == record.Id)
                .ToListAsync(cancellationToken);
            var routes = await _dbContext.Routes
                .Where(value => value.OwnerExtensionId == extensionId)
                .ToListAsync(cancellationToken);
            var services = await _dbContext.Services
                .Where(value => value.OwnerExtensionId == extensionId)
                .ToListAsync(cancellationToken);
            var serviceIds = services.Select(static value => value.Id).ToArray();
            var serviceRuntimes = await _dbContext.ServiceRuntimes
                .Where(value => serviceIds.Contains(value.ServiceId))
                .ToListAsync(cancellationToken);
            var extensionNodeStates = await _dbContext.ExtensionNodeStates
                .Where(value => value.ExtensionRecordId == record.Id)
                .ToListAsync(cancellationToken);
            var portLeases = await _dbContext.PortLeases
                .Where(value => serviceIds.Contains(value.ServiceId))
                .ToListAsync(cancellationToken);
            var blockedByExternalRoute = serviceIds.Length > 0 && await _dbContext.Routes
                .Where(value =>
                    value.ServiceId != null &&
                    serviceIds.Contains(value.ServiceId.Value) &&
                    (value.OwnerExtensionId == null || value.OwnerExtensionId != extensionId))
                .AnyAsync(cancellationToken);
            if (blockedByExternalRoute)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"DeleteExtensionRecordCascade expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision is {record.Version}; an externally owned Route still references one of the extension's Services.");
            }

            _dbContext.ExtensionSettings.RemoveRange(settings);
            _dbContext.Routes.RemoveRange(routes);
            _dbContext.ServiceRuntimes.RemoveRange(serviceRuntimes);
            _dbContext.PortLeases.RemoveRange(portLeases);
            _dbContext.Services.RemoveRange(services);
            _dbContext.ExtensionNodeStates.RemoveRange(extensionNodeStates);
            _dbContext.ExtensionRecords.Remove(record);

            var now = _timeProvider.GetUtcNow();
            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = HostConfigurationWriteContext.CurrentCommittedBy;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion);
            return ConfigurationWriteResult.Success(committedVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "DeleteExtensionRecordCascade", extensionId);
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "DeleteExtensionRecordCascade could not advance the global ConfigurationRevision after removing extension-owned records.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"DeleteExtensionRecordCascade expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision changed during commit.");
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"DeleteExtensionRecordCascade expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or foreign-key conflict while deleting extension-owned entities.");
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"DeleteExtensionRecordCascade expected ExtensionRecord revision {expectedRecordVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or foreign-key conflict while deleting extension-owned entities.");
        }
        catch (InvalidOperationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "DeleteExtensionRecordCascade could not remove extension-owned settings, routes, services, node states, runtimes, or port leases because their relationships are inconsistent.");
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "DeleteExtensionRecordCascade could not delete ExtensionRecord, ExtensionSetting, Route, Service, ExtensionNodeState, ServiceRuntime, or PortLease entities because the database was unavailable or rejected a foreign-key constraint.");
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "DeleteExtensionRecordCascade", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "DeleteExtensionRecordCascade could not delete ExtensionRecord, ExtensionSetting, Route, Service, ExtensionNodeState, ServiceRuntime, or PortLease entities because the database was unavailable or rejected a foreign-key constraint.");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadExtensionOwnedAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId))
        {
            return ConfigurationReadResult<ExtensionConfigurationSnapshot>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Validation,
                    "ReadExtensionOwnedAsync requires a safe extension ID."));
        }

        var full = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!full.IsSuccess || full.Value is not { } snapshot)
        {
            return ConfigurationReadResult<ExtensionConfigurationSnapshot>.Failure(full.Errors.ToArray());
        }

        var routeIds = await _dbContext.Routes.AsNoTracking()
            .Where(value => value.OwnerExtensionId == extensionId)
            .Select(value => value.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var serviceIds = await _dbContext.Services.AsNoTracking()
            .Where(value => value.OwnerExtensionId == extensionId)
            .Select(value => value.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var routeSet = routeIds.ToHashSet();
        var serviceSet = serviceIds.ToHashSet();
        return ConfigurationReadResult<ExtensionConfigurationSnapshot>.Success(
            new ExtensionConfigurationSnapshot(
                snapshot.Version,
                snapshot.Routes.Where(value => routeSet.Contains(value.Id))
                    .Select(MapExtensionRoute).ToImmutableArray(),
                snapshot.Services.Where(value => serviceSet.Contains(value.Id))
                    .Select(MapExtensionService).ToImmutableArray(),
                snapshot.ExtensionSettings.SingleOrDefault(value =>
                    string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal))));
    }

    internal async ValueTask<ConfigurationWriteResult> WriteExtensionOwnedSnapshotAsync(
        string extensionId,
        long expectedVersion,
        ConfigurationChangeSet changes,
        IReadOnlySet<Guid> ownedRouteIds,
        IReadOnlySet<Guid> ownedServiceIds,
        CancellationToken cancellationToken = default)
    {
        var previous = _ownerWriteContext.Value;
        _ownerWriteContext.Value = new OwnerWriteContext(extensionId, ownedRouteIds, ownedServiceIds);
        try
        {
            return await WriteSnapshotAsync(expectedVersion, changes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ownerWriteContext.Value = previous;
        }
    }

    private static ExtensionRouteConfiguration MapExtensionRoute(RouteConfiguration route)
    {
        ExtensionRouteTargetConfiguration target = route.Target switch
        {
            ExtensionHandlerRouteTargetConfiguration handler => new ExtensionHandlerRouteTarget(handler.HandlerId),
            MicroserviceRouteTargetConfiguration service => new ExtensionServiceRouteTarget(service.ServiceId),
            _ => throw new HostConfigurationSemanticValidator.ConfigurationValidationException()
        };
        return new ExtensionRouteConfiguration(route.Id, route.Enabled, route.Matcher, target, route.Priority);
    }

    private static ExtensionServiceConfiguration MapExtensionService(ServiceConfiguration service) =>
        new(
            service.Id,
            service.Enabled,
            service.FileName,
            service.ArgumentList,
            service.WorkingDirectory,
            service.StartMode,
            service.RestartPolicy,
            service.HealthCheck,
            service.CreatedAt,
            service.UpdatedAt,
            service.Version);

    /// <inheritdoc />
    public async ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadExtensionSettingsAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId))
            {
                return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.Validation,
                        "ReadExtensionSettings requires a safe extension ID."));
            }

            var setting = await _dbContext.ExtensionSettings
                .AsNoTracking()
                .Join(
                    _dbContext.ExtensionRecords.AsNoTracking(),
                    setting => setting.ExtensionRecordId,
                    record => record.Id,
                    (setting, record) => new { setting, record })
                .SingleOrDefaultAsync(value => value.record.ExtensionId == extensionId, cancellationToken);
            if (setting is null)
            {
                return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NoSettings,
                        $"No ExtensionSetting document exists for extension '{extensionId}'."));
            }

            var result = new ExtensionSettingsConfiguration(
                setting.record.ExtensionId,
                setting.setting.SchemaVersion,
                HostConfigurationSemanticValidator.NormalizeJson(setting.setting.SettingsJson, null),
                setting.setting.Version);
            if (!HostConfigurationSemanticValidator.TryValidateExtensionSettings(result, _logger))
            {
                return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.Validation,
                        $"ReadExtensionSettings found invalid schema or JSON values in the ExtensionSetting document for extension '{extensionId}'."));
            }
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Success(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "ReadExtensionSettings", extensionId);
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "ReadExtensionSettings", extensionId);
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Validation,
                    $"ReadExtensionSettings found an invalid persisted schema or JSON value for extension '{extensionId}'."));
        }
        catch (ArgumentException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "ReadExtensionSettings", extensionId);
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Validation,
                    $"ReadExtensionSettings could not map the ExtensionSetting document for extension '{extensionId}' because a stored value violates the contract."));
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "ReadExtensionSettings", extensionId);
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.StorageUnavailable,
                    $"ReadExtensionSettings could not read ExtensionSetting or ExtensionRecord data for extension '{extensionId}' from the persistence store."));
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "ReadExtensionSettings", extensionId);
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.StorageUnavailable,
                    $"ReadExtensionSettings could not read ExtensionSetting or ExtensionRecord data for extension '{extensionId}' from the persistence store."));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> WriteExtensionSettingsAsync(
        string extensionId,
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId) ||
                expectedVersion < 0 || settings is null)
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteExtensionSettings requires a safe extension ID, non-negative expected revision, and non-null settings document.");
            }

            if (!HostConfigurationSemanticValidator.TryValidateExtensionSettings(settings, _logger))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    $"WriteExtensionSettings rejected invalid schema, version, or JSON data for extension '{extensionId}'.");
            }
            if (!string.Equals(extensionId, settings.ExtensionId, StringComparison.Ordinal))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteExtensionSettings requires the settings document to match the requested extension ID.");
            }

            if (settings.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"WriteExtensionSettings expected settings revision {expectedVersion}, but the submitted settings revision is {settings.Version}.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
            var record = await _dbContext.ExtensionRecords
                .SingleOrDefaultAsync(value => value.ExtensionId == extensionId, cancellationToken);
            var revision = await _dbContext.ConfigurationRevisions
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            if (record is null || revision is null)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.NotFound,
                        $"ExtensionRecord '{extensionId}' or ConfigurationRevision '{PersistenceDatabaseDefaults.GlobalRevisionKey}' was not found."));
            }

            if (!HostConfigurationSemanticValidator.IsUuidV7(record.Id) ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure(
                    "WriteExtensionSettings found an invalid ExtensionRecord identity or ConfigurationRevision identity.");
            }

            var setting = await _dbContext.ExtensionSettings
                .SingleOrDefaultAsync(value => value.ExtensionRecordId == record.Id, cancellationToken);
            if (setting is null)
            {
                if (expectedVersion != 0)
                {
                    return EfHostConfigRevisionHelper.ConflictWriteFailure(
                        $"WriteExtensionSettings expected settings revision {expectedVersion}, but the actual revision is 0 because no ExtensionSetting exists.");
                }

                var now = _timeProvider.GetUtcNow();
                _dbContext.ExtensionSettings.Add(new ExtensionSetting
                {
                    Id = EfHostConfigRevisionHelper.NewUuidV7(),
                    ExtensionRecordId = record.Id,
                    SchemaVersion = settings.SchemaVersion,
                    SettingsJson = HostConfigurationSemanticValidator.NormalizeJson(settings.SettingsJson, null),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                });
                await _dbContext.SaveChangesAsync(cancellationToken);

                var newRevisionVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
                revision.Version = newRevisionVersion;
                revision.CommittedAt = now;
                revision.UpdatedAt = now;
                revision.CommittedBy = HostConfigurationWriteContext.CurrentCommittedBy;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await _revisionHelper.PublishConfigurationChangedAsync(newRevisionVersion);
                return ConfigurationWriteResult.Success(1);
            }

            if (setting.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure(
                    $"WriteExtensionSettings expected settings revision {expectedVersion}, but the actual revision is {setting.Version}.");
            }

            var settingsJson = HostConfigurationSemanticValidator.NormalizeJson(settings.SettingsJson, null);
            if (setting.SchemaVersion == settings.SchemaVersion &&
                string.Equals(setting.SettingsJson, settingsJson, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return ConfigurationWriteResult.Success(setting.Version);
            }

            var newSettingVersion = EfHostConfigRevisionHelper.IncrementVersion(setting.Version);
            var updateTime = _timeProvider.GetUtcNow();
            setting.SchemaVersion = settings.SchemaVersion;
            setting.SettingsJson = settingsJson;
            setting.UpdatedAt = updateTime;
            setting.Version = newSettingVersion;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = updateTime;
            revision.UpdatedAt = updateTime;
            revision.CommittedBy = HostConfigurationWriteContext.CurrentCommittedBy;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion);
            return ConfigurationWriteResult.Success(newSettingVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "WriteExtensionSettings", extensionId);
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "WriteExtensionSettings could not advance the ExtensionSetting or global ConfigurationRevision beyond its supported revision range.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"WriteExtensionSettings expected settings revision {expectedVersion}, but the actual revision changed during commit.");
        }
        catch (DbUpdateException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"WriteExtensionSettings expected settings revision {expectedVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict.");
        }
        catch (InvalidOperationException exception) when (EfHostConfigRevisionHelper.IsTransactionConflict(exception))
        {
            PersistenceLogMessages.OperationConflict(_logger, exception, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.ConflictWriteFailure(
                $"WriteExtensionSettings expected settings revision {expectedVersion}, but the actual revision could not be confirmed after a serialization, deadlock, or unique-constraint conflict.");
        }
        catch (InvalidOperationException)
        {
            PersistenceLogMessages.ValidationRejected(_logger, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.ValidationWriteFailure(
                "WriteExtensionSettings found an inconsistent ExtensionRecord or ExtensionSetting identity or revision.");
        }
        catch (DbUpdateException exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "WriteExtensionSettings could not persist ExtensionSetting or ConfigurationRevision because the database was unavailable or rejected a unique-key, foreign-key, or schema check constraint.");
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(_logger, exception, "WriteExtensionSettings", extensionId);
            return EfHostConfigRevisionHelper.StorageWriteFailure(
                "WriteExtensionSettings could not persist ExtensionSetting or ConfigurationRevision because the database was unavailable or rejected a unique-key, foreign-key, or schema check constraint.");
        }
        finally
        {
            _gate.Release();
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

}
