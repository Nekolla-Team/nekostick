using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;
using DomainExtensionLoadState = Nekolla.Nekostick.Domain.ExtensionLoadState;
using DomainServiceRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Applies validated route, service, global, and extension changes to EF entities.</summary>
internal sealed class EfHostConfigEntityOperations
{
    private readonly NekostickDbContext _dbContext;

    internal EfHostConfigEntityOperations(NekostickDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    internal void ApplyReplacement(
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
        revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
    }

    internal static bool TryValidateReplacementVersions(
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
        var ratePolicy = RatePolicyPersistenceMapper.ToPersistence(value.ClientIpRatePolicy);
        entity.ClientIpRateTokenLimit = ratePolicy.TokenLimit;
        entity.ClientIpRateTokensPerPeriod = ratePolicy.TokensPerPeriod;
        entity.ClientIpRateReplenishmentPeriodMilliseconds = ratePolicy.ReplenishmentPeriodMilliseconds;
        entity.ClientIpRateQueueLimit = ratePolicy.QueueLimit;
        entity.ClientIpRateRejectionBehavior = ratePolicy.RejectionBehavior;
        entity.ClientIpRateRetryAfterBehavior = ratePolicy.RetryAfterBehavior;
        entity.MaxRequestBodyBytes = value.MaxRequestBodyBytes;
        entity.MaxRequestHeaderBytes = value.MaxRequestHeaderBytes;
        entity.MaxConcurrentRequests = value.MaxConcurrentRequests;
        entity.RequestReadTimeoutMilliseconds = value.RequestReadTimeout is { } requestReadTimeout
            ? checked((int)requestReadTimeout.TotalMilliseconds)
            : null;
        var retryPolicy = ProxyRetryPersistenceMapper.ToNullablePersistence(value.ProxyRetries);
        entity.ProxyMaxRetries = retryPolicy.MaxRetries;
        entity.ProxyInitialRetryBackoffMilliseconds = retryPolicy.InitialBackoffMilliseconds;
        entity.ProxyMaximumRetryBackoffMilliseconds = retryPolicy.MaximumBackoffMilliseconds;
        entity.ProxyRetryOnConnectionFailure = retryPolicy.RetryOnConnectionFailure;
        entity.ProxyRetryOnUpstreamDisconnect = retryPolicy.RetryOnUpstreamDisconnect;
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
            entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
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
            entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
        }
    }

    private static ExtensionRecord ToExtensionRecordEntity(ExtensionRecordConfiguration value, DateTimeOffset now) =>
        new()
        {
            Id = EfHostConfigRevisionHelper.NewUuidV7(),
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
        entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
    }

    private static ExtensionSetting ToExtensionSettingEntity(
        ExtensionSettingsConfiguration value,
        Guid extensionRecordId,
        DateTimeOffset now) =>
        new()
        {
            Id = EfHostConfigRevisionHelper.NewUuidV7(),
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
        entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
    }

    private static void UpdateGlobalSettings(
        GlobalSettings entity,
        GlobalSettingsConfiguration value,
        DateTimeOffset now)
    {
        entity.AutoPortRangeStart = value.AutoPortRangeStart;
        entity.AutoPortRangeEnd = value.AutoPortRangeEnd;
        entity.MaxRequestBodyBytes = value.MaxRequestBodyBytes;
        entity.MaxRequestHeaderBytes = value.MaxRequestHeaderBytes;
        entity.MaxConcurrentRequests = value.MaxConcurrentRequests;
        entity.ConfigurationPollIntervalSeconds = checked((int)value.ConfigurationPollInterval.TotalSeconds);
        entity.RequestReadTimeoutMilliseconds = checked((int)value.RequestReadTimeout.TotalMilliseconds);
        entity.TrustedProxyCidrsJson = HostConfigurationSemanticValidator.SerializeJson(value.TrustedProxyCidrs);
        entity.ConnectTimeoutMilliseconds = checked((int)value.ProxyTimeouts.ConnectTimeout.TotalMilliseconds);
        entity.HttpActivityTimeoutMilliseconds = checked((int)value.ProxyTimeouts.HttpActivityTimeout.TotalMilliseconds);
        entity.HttpTotalTimeoutMilliseconds = checked((int)value.ProxyTimeouts.HttpTotalTimeout.TotalMilliseconds);
        entity.WebSocketIdleTimeoutMilliseconds = checked((int)value.ProxyTimeouts.WebSocketIdleTimeout.TotalMilliseconds);
        var ratePolicy = RatePolicyPersistenceMapper.ToPersistence(value.ClientIpRatePolicy);
        entity.ClientIpRateTokenLimit = ratePolicy.TokenLimit;
        entity.ClientIpRateTokensPerPeriod = ratePolicy.TokensPerPeriod;
        entity.ClientIpRateReplenishmentPeriodMilliseconds = ratePolicy.ReplenishmentPeriodMilliseconds;
        entity.ClientIpRateQueueLimit = ratePolicy.QueueLimit;
        entity.ClientIpRateRejectionBehavior = ratePolicy.RejectionBehavior;
        entity.ClientIpRateRetryAfterBehavior = ratePolicy.RetryAfterBehavior;
        var retryPolicy = ProxyRetryPersistenceMapper.ToPersistence(value.ProxyRetries);
        entity.ProxyMaxRetries = retryPolicy.MaxRetries;
        entity.ProxyInitialRetryBackoffMilliseconds = retryPolicy.InitialBackoffMilliseconds;
        entity.ProxyMaximumRetryBackoffMilliseconds = retryPolicy.MaximumBackoffMilliseconds;
        entity.ProxyRetryOnConnectionFailure = retryPolicy.RetryOnConnectionFailure;
        entity.ProxyRetryOnUpstreamDisconnect = retryPolicy.RetryOnUpstreamDisconnect;
        entity.UpdatedAt = now;
        entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
    }
}
