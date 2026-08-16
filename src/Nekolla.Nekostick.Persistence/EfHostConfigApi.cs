using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;
using ContractExtensionLoadState = Nekolla.Nekostick.Contracts.ExtensionLoadState;
using ContractServiceRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using DomainExtensionLoadState = Nekolla.Nekostick.Domain.ExtensionLoadState;
using DomainServiceRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Provides the transactional, PostgreSQL-backed host configuration boundary.</summary>
public sealed class EfHostConfigApi : IHostConfigApi, IAsyncDisposable
{
    private const string ConfigurationChangedChannel = "nekostick_config_changed";
    private const string Committer = "host-config-api";
    private readonly NekostickDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the EF-backed host configuration API.</summary>
    /// <param name="dbContext">The scoped PostgreSQL context owned by the host.</param>
    /// <param name="timeProvider">The clock used for persisted timestamps.</param>
    public EfHostConfigApi(NekostickDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            var snapshot = MapSnapshot(
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
                return ValidationWriteFailure();
            }

            if (!HostConfigurationSemanticValidator.TryValidateChangeSet(changes))
            {
                return ValidationWriteFailure();
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
                return ValidationWriteFailure();
            }

            if (revision.Version != expectedVersion || changes.GlobalSettings.Version != globalSettings.Version)
            {
                return ConflictWriteFailure();
            }

            var routes = await _dbContext.Routes.ToListAsync(cancellationToken);
            var services = await _dbContext.Services.ToListAsync(cancellationToken);
            var extensionRecords = await _dbContext.ExtensionRecords.ToListAsync(cancellationToken);
            var extensionSettings = await _dbContext.ExtensionSettings.ToListAsync(cancellationToken);

            if (!TryValidateReplacementVersions(
                    changes,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings,
                    globalSettings,
                    out var versionsAreValid))
            {
                return ValidationWriteFailure();
            }

            if (!versionsAreValid)
            {
                return ConflictWriteFailure();
            }

            var removedServiceIds = services
                .Select(value => value.Id)
                .Except(changes.Services.Select(value => value.Id))
                .ToArray();
            if (removedServiceIds.Length != 0 && await _dbContext.PortLeases
                    .AsNoTracking()
                    .AnyAsync(value => removedServiceIds.Contains(value.ServiceId), cancellationToken))
            {
                return ValidationWriteFailure();
            }

            var now = _timeProvider.GetUtcNow();
            ApplyReplacement(
                changes,
                revision,
                globalSettings,
                routes,
                services,
                extensionRecords,
                extensionSettings,
                now);

            var newVersion = IncrementVersion(revision.Version);
            revision.Version = newVersion;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await PublishConfigurationChangedAsync(newVersion);
            return ConfigurationWriteResult.Success(newVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            return ValidationWriteFailure();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictWriteFailure();
        }
        catch (DbUpdateException exception) when (IsTransactionConflict(exception))
        {
            return ConflictWriteFailure();
        }
        catch (InvalidOperationException)
        {
            return ValidationWriteFailure();
        }
        catch (DbUpdateException)
        {
            return StorageWriteFailure();
        }
        catch (Exception)
        {
            return StorageWriteFailure();
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
                return ValidationWriteFailure();
            }

            if (!HostConfigurationSemanticValidator.TryValidateExtensionSettings(settings))
            {
                return ValidationWriteFailure();
            }
            if (!string.Equals(extensionId, settings.ExtensionId, StringComparison.Ordinal))
            {
                return ValidationWriteFailure();
            }

            if (settings.Version != expectedVersion)
            {
                return ConflictWriteFailure();
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
                return ValidationWriteFailure();
            }

            var setting = await _dbContext.ExtensionSettings
                .SingleOrDefaultAsync(value => value.ExtensionRecordId == record.Id, cancellationToken);
            if (setting is null)
            {
                if (expectedVersion != 0)
                {
                    return ConflictWriteFailure();
                }

                var now = _timeProvider.GetUtcNow();
                _dbContext.ExtensionSettings.Add(new ExtensionSetting
                {
                    Id = NewUuidV7(),
                    ExtensionRecordId = record.Id,
                    SchemaVersion = settings.SchemaVersion,
                    SettingsJson = HostConfigurationSemanticValidator.NormalizeJson(settings.SettingsJson, null),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                });
                await _dbContext.SaveChangesAsync(cancellationToken);

                var newRevisionVersion = IncrementVersion(revision.Version);
                revision.Version = newRevisionVersion;
                revision.CommittedAt = now;
                revision.UpdatedAt = now;
                revision.CommittedBy = Committer;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await PublishConfigurationChangedAsync(newRevisionVersion);
                return ConfigurationWriteResult.Success(1);
            }

            if (setting.Version != expectedVersion)
            {
                return ConflictWriteFailure();
            }

            var newSettingVersion = IncrementVersion(setting.Version);
            var updateTime = _timeProvider.GetUtcNow();
            setting.SchemaVersion = settings.SchemaVersion;
            setting.SettingsJson = HostConfigurationSemanticValidator.NormalizeJson(settings.SettingsJson, null);
            setting.UpdatedAt = updateTime;
            setting.Version = newSettingVersion;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var committedVersion = IncrementVersion(revision.Version);
            revision.Version = committedVersion;
            revision.CommittedAt = updateTime;
            revision.UpdatedAt = updateTime;
            revision.CommittedBy = Committer;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await PublishConfigurationChangedAsync(committedVersion);
            return ConfigurationWriteResult.Success(newSettingVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HostConfigurationSemanticValidator.ConfigurationValidationException)
        {
            return ValidationWriteFailure();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictWriteFailure();
        }
        catch (DbUpdateException exception) when (IsTransactionConflict(exception))
        {
            return ConflictWriteFailure();
        }
        catch (InvalidOperationException)
        {
            return ValidationWriteFailure();
        }
        catch (DbUpdateException)
        {
            return StorageWriteFailure();
        }
        catch (Exception)
        {
            return StorageWriteFailure();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HostConfigurationSnapshot MapSnapshot(
        ConfigurationRevision revision,
        GlobalSettings globalSettings,
        IReadOnlyList<Route> routes,
        IReadOnlyList<Service> services,
        IReadOnlyList<ExtensionRecord> extensionRecords,
        IReadOnlyList<ExtensionSetting> extensionSettings)
    {
        var extensionIds = extensionRecords.ToDictionary(value => value.Id, value => value.ExtensionId);
        var mappedSettings = extensionSettings
            .Select(value =>
            {
                if (!extensionIds.TryGetValue(value.ExtensionRecordId, out var extensionId))
                {
                    throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
                }

                return new ExtensionSettingsConfiguration(
                    extensionId,
                    value.SchemaVersion,
                    HostConfigurationSemanticValidator.NormalizeJson(value.SettingsJson, null),
                    value.Version);
            })
            .ToImmutableArray();

        return new HostConfigurationSnapshot(
            revision.Version,
            MapGlobalSettings(globalSettings),
            routes.Select(MapRoute).ToImmutableArray(),
            services.Select(MapService).ToImmutableArray(),
            extensionRecords
                .Select(value => new ExtensionRecordConfiguration(
                    value.ExtensionId,
                    value.InstalledVersion,
                    (ContractExtensionLoadState)value.LoadState,
                    value.CreatedAt,
                    value.UpdatedAt,
                    value.Version))
                .ToImmutableArray(),
            mappedSettings);
    }

    private static GlobalSettingsConfiguration MapGlobalSettings(GlobalSettings value) =>
        new(
            value.Version,
            value.AutoPortRangeStart,
            value.AutoPortRangeEnd,
            value.MaxRequestBodyBytes,
            value.MaxConcurrentRequests,
            TimeSpan.FromSeconds(value.ConfigurationPollIntervalSeconds),
            HostConfigurationSemanticValidator.DeserializeStringArray(value.TrustedProxyCidrsJson));

    private static RouteConfiguration MapRoute(Route value)
    {
        ValidatePersistedTarget(value);
        RouteTargetConfiguration target = value.TargetType switch
        {
            RouteTargetKind.Microservice => new MicroserviceRouteTargetConfiguration(value.ServiceId ?? Guid.Empty),
            RouteTargetKind.StaticFile => new StaticFileRouteTargetConfiguration(value.StaticRootPath ?? string.Empty),
            RouteTargetKind.ExtensionHandler => new ExtensionHandlerRouteTargetConfiguration(value.ExtensionHandlerId ?? string.Empty),
            _ => throw new HostConfigurationSemanticValidator.ConfigurationValidationException()
        };

        return new RouteConfiguration(
            value.Id,
            value.Enabled,
            new RouteMatcherConfiguration(
                (RouteMatcherType)value.MatcherType,
                value.Pattern,
                HostConfigurationSemanticValidator.DeserializeStringArray(value.HostPatternsJson),
                HostConfigurationSemanticValidator.DeserializeStringArray(value.MethodsJson)),
            target,
            value.Priority,
            new ForwardingConfiguration(
                (ForwardingMode)value.ForwardingMode,
                value.ReplaceTemplate),
            HostConfigurationSemanticValidator.DeserializeHeaderRewrites(value.RequestHeaderRewritesJson),
            HostConfigurationSemanticValidator.DeserializeHeaderRewrites(value.ResponseHeaderRewritesJson),
            HostConfigurationSemanticValidator.NormalizeJson(value.MetadataJson, JsonValueKind.Object),
            value.CreatedAt,
            value.UpdatedAt,
            value.Version);
    }

    private static void ValidatePersistedTarget(Route value)
    {
        var valid = value.TargetType switch
        {
            RouteTargetKind.Microservice => value.ServiceId is not null &&
                string.Equals(value.TargetId, value.ServiceId.Value.ToString("D"), StringComparison.Ordinal) &&
                value.StaticRootPath is null && value.ExtensionHandlerId is null,
            RouteTargetKind.StaticFile => value.ServiceId is null && value.StaticRootPath is not null &&
                string.Equals(value.TargetId, value.StaticRootPath, StringComparison.Ordinal) &&
                value.ExtensionHandlerId is null,
            RouteTargetKind.ExtensionHandler => value.ServiceId is null && value.StaticRootPath is null &&
                value.ExtensionHandlerId is not null &&
                string.Equals(value.TargetId, value.ExtensionHandlerId, StringComparison.Ordinal),
            _ => false
        };
        if (!valid)
        {
            throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
        }
    }

    private static ServiceConfiguration MapService(Service value) =>
        new(
            value.Id,
            value.Enabled,
            value.FileName,
            HostConfigurationSemanticValidator.DeserializeStringArray(value.ArgumentListJson),
            value.WorkingDirectory,
            HostConfigurationSemanticValidator.DeserializeEnvironment(value.EnvironmentJson),
            (ServiceStartMode)value.StartMode,
            (ContractServiceRestartPolicy)value.RestartPolicy,
            new ServiceHealthCheckConfiguration(
                (ServiceHealthCheckType)value.HealthCheckType,
                value.HealthCheckHttpPath,
                TimeSpan.FromMilliseconds(value.HealthCheckTimeoutMilliseconds)),
            value.CreatedAt,
            value.UpdatedAt,
            value.Version);

    private void ApplyReplacement(
        ConfigurationChangeSet changes,
        ConfigurationRevision revision,
        GlobalSettings globalSettings,
        List<Route> routes,
        List<Service> services,
        List<ExtensionRecord> extensionRecords,
        List<ExtensionSetting> extensionSettings,
        DateTimeOffset now)
    {
        var incomingRouteIds = changes.Routes.Select(value => value.Id).ToHashSet();
        var incomingServiceIds = changes.Services.Select(value => value.Id).ToHashSet();
        var incomingExtensionIds = changes.ExtensionRecords
            .Select(value => value.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);
        var incomingSettingIds = changes.ExtensionSettings
            .Select(value => value.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);

        _dbContext.Routes.RemoveRange(routes.Where(value => !incomingRouteIds.Contains(value.Id)));
        _dbContext.ExtensionSettings.RemoveRange(extensionSettings.Where(value =>
            !incomingSettingIds.Contains(
                extensionRecords.Single(record => record.Id == value.ExtensionRecordId).ExtensionId)));
        _dbContext.ExtensionRecords.RemoveRange(extensionRecords.Where(value =>
            !incomingExtensionIds.Contains(value.ExtensionId)));
        _dbContext.Services.RemoveRange(services.Where(value => !incomingServiceIds.Contains(value.Id)));

        UpdateGlobalSettings(globalSettings, changes.GlobalSettings, now);

        foreach (var route in changes.Routes)
        {
            var entity = routes.FirstOrDefault(value => value.Id == route.Id);
            if (entity is null)
            {
                _dbContext.Routes.Add(ToRouteEntity(route, now));
            }
            else
            {
                UpdateRoute(entity, route, now);
            }
        }

        foreach (var service in changes.Services)
        {
            var entity = services.FirstOrDefault(value => value.Id == service.Id);
            if (entity is null)
            {
                _dbContext.Services.Add(ToServiceEntity(service, now));
            }
            else
            {
                UpdateService(entity, service, now);
            }
        }

        var extensionRecordIds = extensionRecords.ToDictionary(
            value => value.ExtensionId,
            value => value.Id,
            StringComparer.Ordinal);
        foreach (var extension in changes.ExtensionRecords)
        {
            var entity = extensionRecords.FirstOrDefault(value =>
                string.Equals(value.ExtensionId, extension.ExtensionId, StringComparison.Ordinal));
            if (entity is null)
            {
                entity = ToExtensionRecordEntity(extension, now);
                _dbContext.ExtensionRecords.Add(entity);
            }
            else
            {
                UpdateExtensionRecord(entity, extension, now);
            }

            extensionRecordIds[extension.ExtensionId] = entity.Id;
        }

        foreach (var setting in changes.ExtensionSettings)
        {
            var extensionRecordId = extensionRecordIds[setting.ExtensionId];

            var entity = extensionSettings.FirstOrDefault(value =>
                value.ExtensionRecordId == extensionRecordId);
            if (entity is null)
            {
                _dbContext.ExtensionSettings.Add(ToExtensionSettingEntity(setting, extensionRecordId, now));
            }
            else
            {
                UpdateExtensionSetting(entity, setting, now);
            }
        }

        revision.CommittedAt = now;
        revision.UpdatedAt = now;
        revision.CommittedBy = Committer;
    }

    private static bool TryValidateReplacementVersions(
        ConfigurationChangeSet changes,
        IReadOnlyList<Route> routes,
        IReadOnlyList<Service> services,
        IReadOnlyList<ExtensionRecord> extensionRecords,
        IReadOnlyList<ExtensionSetting> extensionSettings,
        GlobalSettings globalSettings,
        out bool versionsAreValid)
    {
        versionsAreValid = true;
        if (changes.GlobalSettings.Version != globalSettings.Version)
        {
            versionsAreValid = false;
            return true;
        }

        versionsAreValid = VersionsMatch(
            changes.Routes,
            routes,
            value => value.Id,
            value => value.Version,
            value => value.Id,
            value => value.Version)
            && VersionsMatch(
                changes.Services,
                services,
                value => value.Id,
                value => value.Version,
                value => value.Id,
                value => value.Version)
            && VersionsMatch(
                changes.ExtensionRecords,
                extensionRecords,
                value => value.ExtensionId,
                value => value.RecordVersion,
                value => value.ExtensionId,
                value => value.Version)
            && SettingsVersionsMatch(changes.ExtensionSettings, extensionSettings, extensionRecords);
        return true;
    }

    private static bool VersionsMatch<TValue, TEntity, TKey>(
        IEnumerable<TValue> incoming,
        IEnumerable<TEntity> existing,
        Func<TValue, TKey> incomingKey,
        Func<TValue, long> incomingVersion,
        Func<TEntity, TKey> existingKey,
        Func<TEntity, long> existingVersion)
        where TKey : notnull
    {
        var existingByKey = existing.ToDictionary(existingKey, existingVersion);
        foreach (var item in incoming)
        {
            var key = incomingKey(item);
            if (existingByKey.TryGetValue(key, out var version))
            {
                if (incomingVersion(item) != version)
                {
                    return false;
                }
            }
            else if (incomingVersion(item) is not (0 or 1))
            {
                throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
            }
        }

        return true;
    }

    private static bool SettingsVersionsMatch(
        IEnumerable<ExtensionSettingsConfiguration> incoming,
        IEnumerable<ExtensionSetting> existing,
        IEnumerable<ExtensionRecord> extensionRecords)
    {
        var recordIds = extensionRecords.ToDictionary(
            value => value.ExtensionId,
            value => value.Id,
            StringComparer.Ordinal);
        var existingByExtension = existing.ToDictionary(value => value.ExtensionRecordId);
        foreach (var setting in incoming)
        {
            if (!recordIds.TryGetValue(setting.ExtensionId, out var recordId))
            {
                if (setting.Version is not (0 or 1))
                {
                    throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
                }

                continue;
            }

            if (existingByExtension.TryGetValue(recordId, out var current))
            {
                if (setting.Version != current.Version)
                {
                    return false;
                }
            }
            else if (setting.Version is not (0 or 1))
            {
                throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
            }
        }

        return true;
    }

    private static Route ToRouteEntity(RouteConfiguration value, DateTimeOffset now)
    {
        var entity = new Route { Id = value.Id, Version = 1, CreatedAt = now, UpdatedAt = now };
        UpdateRoute(entity, value, now, false);
        return entity;
    }

    private static void UpdateRoute(Route entity, RouteConfiguration value, DateTimeOffset now) =>
        UpdateRoute(entity, value, now, true);

    private static void UpdateRoute(Route entity, RouteConfiguration value, DateTimeOffset now, bool incrementVersion)
    {
        entity.Enabled = value.Enabled;
        entity.MatcherType = (RouteMatcherKind)value.Matcher.Type;
        entity.Pattern = value.Matcher.Pattern;
        entity.HostPatternsJson = HostConfigurationSemanticValidator.SerializeJson(value.Matcher.HostPatterns);
        entity.MethodsJson = HostConfigurationSemanticValidator.SerializeJson(value.Matcher.Methods);
        entity.Priority = value.Priority;
        entity.ForwardingMode = (ForwardingKind)value.Forwarding.Mode;
        entity.ReplaceTemplate = value.Forwarding.ReplaceTemplate;
        entity.RequestHeaderRewritesJson = HostConfigurationSemanticValidator.SerializeJson(value.RequestHeaderRewrites);
        entity.ResponseHeaderRewritesJson = HostConfigurationSemanticValidator.SerializeJson(value.ResponseHeaderRewrites);
        entity.MetadataJson = HostConfigurationSemanticValidator.NormalizeJson(value.MetadataJson, JsonValueKind.Object);
        switch (value.Target)
        {
            case MicroserviceRouteTargetConfiguration microservice:
                entity.TargetType = RouteTargetKind.Microservice;
                entity.TargetId = microservice.ServiceId.ToString("D");
                entity.ServiceId = microservice.ServiceId;
                entity.StaticRootPath = null;
                entity.ExtensionHandlerId = null;
                break;
            case StaticFileRouteTargetConfiguration staticFile:
                entity.TargetType = RouteTargetKind.StaticFile;
                entity.TargetId = staticFile.RootPath;
                entity.ServiceId = null;
                entity.StaticRootPath = staticFile.RootPath;
                entity.ExtensionHandlerId = null;
                break;
            case ExtensionHandlerRouteTargetConfiguration handler:
                entity.TargetType = RouteTargetKind.ExtensionHandler;
                entity.TargetId = handler.HandlerId;
                entity.ServiceId = null;
                entity.StaticRootPath = null;
                entity.ExtensionHandlerId = handler.HandlerId;
                break;
            default:
                throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
        }

        entity.UpdatedAt = now;
        if (incrementVersion)
        {
            entity.Version = IncrementVersion(entity.Version);
        }
    }

    private static Service ToServiceEntity(ServiceConfiguration value, DateTimeOffset now)
    {
        var entity = new Service { Id = value.Id, Version = 1, CreatedAt = now, UpdatedAt = now };
        UpdateService(entity, value, now, false);
        return entity;
    }

    private static void UpdateService(Service entity, ServiceConfiguration value, DateTimeOffset now) =>
        UpdateService(entity, value, now, true);

    private static void UpdateService(Service entity, ServiceConfiguration value, DateTimeOffset now, bool incrementVersion)
    {
        entity.Enabled = value.Enabled;
        entity.FileName = value.FileName;
        entity.ArgumentListJson = HostConfigurationSemanticValidator.SerializeJson(value.ArgumentList);
        entity.WorkingDirectory = value.WorkingDirectory;
        entity.EnvironmentJson = HostConfigurationSemanticValidator.SerializeEnvironment(value.Environment);
        entity.StartMode = (ServiceStartPolicy)value.StartMode;
        entity.RestartPolicy = (DomainServiceRestartPolicy)value.RestartPolicy;
        entity.HealthCheckType = (ServiceHealthCheckKind)value.HealthCheck.Type;
        entity.HealthCheckHttpPath = value.HealthCheck.HttpPath;
        entity.HealthCheckTimeoutMilliseconds = checked((int)value.HealthCheck.Timeout.TotalMilliseconds);
        entity.UpdatedAt = now;
        if (incrementVersion)
        {
            entity.Version = IncrementVersion(entity.Version);
        }
    }

    private static ExtensionRecord ToExtensionRecordEntity(ExtensionRecordConfiguration value, DateTimeOffset now) =>
        new()
        {
            Id = NewUuidV7(),
            ExtensionId = value.ExtensionId,
            InstalledVersion = value.Version,
            LoadState = (DomainExtensionLoadState)value.LoadState,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

    private static void UpdateExtensionRecord(ExtensionRecord entity, ExtensionRecordConfiguration value, DateTimeOffset now)
    {
        entity.InstalledVersion = value.Version;
        entity.LoadState = (DomainExtensionLoadState)value.LoadState;
        entity.UpdatedAt = now;
        entity.Version = IncrementVersion(entity.Version);
    }

    private static ExtensionSetting ToExtensionSettingEntity(
        ExtensionSettingsConfiguration value,
        Guid extensionRecordId,
        DateTimeOffset now) =>
        new()
        {
            Id = NewUuidV7(),
            ExtensionRecordId = extensionRecordId,
            SchemaVersion = value.SchemaVersion,
            SettingsJson = HostConfigurationSemanticValidator.NormalizeJson(value.SettingsJson, null),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

    private static void UpdateExtensionSetting(
        ExtensionSetting entity,
        ExtensionSettingsConfiguration value,
        DateTimeOffset now)
    {
        entity.SchemaVersion = value.SchemaVersion;
        entity.SettingsJson = HostConfigurationSemanticValidator.NormalizeJson(value.SettingsJson, null);
        entity.UpdatedAt = now;
        entity.Version = IncrementVersion(entity.Version);
    }

    private static void UpdateGlobalSettings(
        GlobalSettings entity,
        GlobalSettingsConfiguration value,
        DateTimeOffset now)
    {
        entity.AutoPortRangeStart = value.AutoPortRangeStart;
        entity.AutoPortRangeEnd = value.AutoPortRangeEnd;
        entity.MaxRequestBodyBytes = value.MaxRequestBodyBytes;
        entity.MaxConcurrentRequests = value.MaxConcurrentRequests;
        entity.ConfigurationPollIntervalSeconds = checked((int)value.ConfigurationPollInterval.TotalSeconds);
        entity.TrustedProxyCidrsJson = HostConfigurationSemanticValidator.SerializeJson(value.TrustedProxyCidrs);
        entity.UpdatedAt = now;
        entity.Version = IncrementVersion(entity.Version);
    }

    private async Task PublishConfigurationChangedAsync(long version)
    {
        try
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await _dbContext.Database.OpenConnectionAsync(CancellationToken.None);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_notify(@channel, @payload);";
            var channel = command.CreateParameter();
            channel.ParameterName = "channel";
            channel.DbType = System.Data.DbType.String;
            channel.Value = ConfigurationChangedChannel;
            command.Parameters.Add(channel);
            var payload = command.CreateParameter();
            payload.ParameterName = "payload";
            payload.DbType = System.Data.DbType.String;
            payload.Value = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.Add(payload);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Polling the singleton revision is the durable notification fallback.
        }
    }

    private static long IncrementVersion(long version) =>
        version == long.MaxValue
            ? throw new HostConfigurationSemanticValidator.ConfigurationValidationException()
            : checked(version + 1);

    private static Guid NewUuidV7() => Guid.CreateVersion7();

    private static ConfigurationWriteResult ValidationWriteFailure() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));

    private static ConfigurationWriteResult ConflictWriteFailure() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));

    private static ConfigurationWriteResult StorageWriteFailure() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));

    private static bool IsTransactionConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

}
