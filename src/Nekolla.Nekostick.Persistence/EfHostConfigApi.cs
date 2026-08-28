using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Provides the transactional, PostgreSQL-backed host configuration boundary.</summary>
public sealed class EfHostConfigApi : IHostConfigApi, IAsyncDisposable
{
    private readonly NekostickDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
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
    public EfHostConfigApi(NekostickDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entityOperations = new EfHostConfigEntityOperations(_dbContext);
        _revisionHelper = new EfHostConfigRevisionHelper(_dbContext);
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
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
            if (!HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot))
            {
                return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }

            return ConfigurationReadResult<HostConfigurationSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }
        catch (ArgumentException)
        {
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }
        catch (DbUpdateException)
        {
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
        }
        catch (Exception)
        {
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
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
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            if (!HostConfigurationSemanticValidator.TryValidateChangeSet(changes))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
            }

            if (revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId) ||
                globalSettings.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            if (revision.Version != expectedVersion || changes.GlobalSettings.Version != globalSettings.Version)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
            }

            var routes = await _dbContext.Routes.ToListAsync(cancellationToken);
            var services = await _dbContext.Services.ToListAsync(cancellationToken);
            var extensionRecords = await _dbContext.ExtensionRecords.ToListAsync(cancellationToken);
            var extensionSettings = await _dbContext.ExtensionSettings.ToListAsync(cancellationToken);

            if (!EfHostConfigEntityOperations.TryValidateReplacementVersions(
                    changes,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    globalSettings,
                    out var versionsAreValid))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            if (!versionsAreValid)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
            }

            var removedServiceIds = services
                .Select(value => value.Id)
                .Except(changes.Services.Select(value => value.Id))
                .ToArray();
            if (removedServiceIds.Length != 0 && await _dbContext.PortLeases
                    .AsNoTracking()
                    .AnyAsync(value => removedServiceIds.Contains(value.ServiceId), cancellationToken))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            var now = _timeProvider.GetUtcNow();
            if (_ownerWriteContext.Value is { } ownerContext)
            {
                _entityOperations.ApplyReplacement(
                    changes,
                    revision,
                    globalSettings,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    now,
                    ownerContext.ExtensionId,
                    ownerContext.RouteIds,
                    ownerContext.ServiceIds);
            }
            else
            {
                _entityOperations.ApplyReplacement(
                    changes,
                    revision,
                    globalSettings,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    now);
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
                !HostConfigurationSemanticValidator.TryValidateExtensionRecords(records))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
            }

            if (revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId) ||
                globalSettings.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            // The revision is checked before the idempotence path so a stale caller cannot
            // mistake a concurrent commit for a successful no-op.
            if (revision.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
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
                // A different installed version/state is a real cross-process conflict.
                if (!string.Equals(existingRecord.InstalledVersion, record.Version, StringComparison.Ordinal) ||
                    (Nekolla.Nekostick.Contracts.ExtensionLoadState)existingRecord.LoadState != initialState)
                {
                    return EfHostConfigRevisionHelper.ConflictWriteFailure();
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
                LoadState = (Nekolla.Nekostick.Domain.ExtensionLoadState)initialState,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            }));

            var newVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = newVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(newVersion);
            return ConfigurationWriteResult.Success(newVersion);
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
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
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

            var currentState = (ExtensionLoadState)record.LoadState;
            if (!Enum.IsDefined(currentState) || !IsAllowedExtensionLoadStateTransition(currentState, state))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            var now = _timeProvider.GetUtcNow();
            record.LoadState = (Nekolla.Nekostick.Domain.ExtensionLoadState)state;
            record.UpdatedAt = now;
            record.Version = EfHostConfigRevisionHelper.IncrementVersion(record.Version);

            var newVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = newVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(newVersion);
            return ConfigurationWriteResult.Success(newVersion);
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
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
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

            var now = _timeProvider.GetUtcNow();
            record.InstalledVersion = newVersion;
            record.UpdatedAt = now;
            record.Version = EfHostConfigRevisionHelper.IncrementVersion(record.Version);

            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion);
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
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
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
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
            }

            _dbContext.ExtensionSettings.RemoveRange(settings);
            _dbContext.Routes.RemoveRange(routes);
            _dbContext.ServiceRuntimes.RemoveRange(serviceRuntimes);
            _dbContext.PortLeases.RemoveRange(portLeases);
            _dbContext.Services.RemoveRange(services);
            _dbContext.ExtensionRecords.Remove(record);

            var now = _timeProvider.GetUtcNow();
            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion);
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
                new ConfigurationError(ConfigurationErrorCode.Validation));
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
                    new ConfigurationError(ConfigurationErrorCode.Validation));
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
            }

            var result = new ExtensionSettingsConfiguration(
                setting.record.ExtensionId,
                setting.setting.SchemaVersion,
                HostConfigurationSemanticValidator.NormalizeJson(setting.setting.SettingsJson, null),
                setting.setting.Version);
            if (!HostConfigurationSemanticValidator.TryValidateExtensionSettings(result))
            {
                return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Success(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }
        catch (ArgumentException)
        {
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }
        catch (DbUpdateException)
        {
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
        }
        catch (Exception)
        {
            return ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
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
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            if (!HostConfigurationSemanticValidator.TryValidateExtensionSettings(settings))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }
            if (!string.Equals(extensionId, settings.ExtensionId, StringComparison.Ordinal))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            if (settings.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
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
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
            }

            if (!HostConfigurationSemanticValidator.IsUuidV7(record.Id) ||
                revision.Id != Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId))
            {
                return EfHostConfigRevisionHelper.ValidationWriteFailure();
            }

            var setting = await _dbContext.ExtensionSettings
                .SingleOrDefaultAsync(value => value.ExtensionRecordId == record.Id, cancellationToken);
            if (setting is null)
            {
                if (expectedVersion != 0)
                {
                    return EfHostConfigRevisionHelper.ConflictWriteFailure();
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
                revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await _revisionHelper.PublishConfigurationChangedAsync(newRevisionVersion);
                return ConfigurationWriteResult.Success(1);
            }

            if (setting.Version != expectedVersion)
            {
                return EfHostConfigRevisionHelper.ConflictWriteFailure();
            }

            var newSettingVersion = EfHostConfigRevisionHelper.IncrementVersion(setting.Version);
            var updateTime = _timeProvider.GetUtcNow();
            setting.SchemaVersion = settings.SchemaVersion;
            setting.SettingsJson = HostConfigurationSemanticValidator.NormalizeJson(settings.SettingsJson, null);
            setting.UpdatedAt = updateTime;
            setting.Version = newSettingVersion;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var committedVersion = EfHostConfigRevisionHelper.IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = updateTime;
            revision.UpdatedAt = updateTime;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await _revisionHelper.PublishConfigurationChangedAsync(committedVersion);
            return ConfigurationWriteResult.Success(newSettingVersion);
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
