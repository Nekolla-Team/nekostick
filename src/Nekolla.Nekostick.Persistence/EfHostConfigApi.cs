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
            _entityOperations.ApplyReplacement(
                changes,
                revision,
                globalSettings,
                routes,
                services,
                extensionRecords,
                extensionSettings,
                now);

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
}
