using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;
using PersistenceExtensionNodeState = Nekolla.Nekostick.Persistence.Entities.ExtensionNodeState;
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

    internal bool ApplyReplacement(
        ConfigurationChangeSet changes,
        ConfigurationRevision revision,
        GlobalSettings globalSettings,
        List<Route> routes,
        List<Service> services,
        List<ExtensionRecord> extensionRecords,
        List<ExtensionSetting> extensionSettings,
        List<PersistenceExtensionNodeState> extensionNodeStates,
        DateTimeOffset now,
        string? ownerExtensionId = null,
        IReadOnlySet<Guid>? ownedRouteIds = null,
        IReadOnlySet<Guid>? ownedServiceIds = null)
    {
        var changed = false;
        var incomingRouteIds = changes.Routes.Select(value => value.Id).ToHashSet();
        var incomingServiceIds = changes.Services.Select(value => value.Id).ToHashSet();
        var incomingExtensionIds = changes.ExtensionRecords
            .Select(value => value.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);
        var incomingSettingIds = changes.ExtensionSettings
            .Select(value => value.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);

        var removedRoutes = routes.Where(value => !incomingRouteIds.Contains(value.Id)).ToArray();
        if (removedRoutes.Length != 0)
        {
            _dbContext.Routes.RemoveRange(removedRoutes);
            changed = true;
        }

        var removedSettings = extensionSettings
            .Where(value => !incomingSettingIds.Contains(
                extensionRecords.Single(record => record.Id == value.ExtensionRecordId).ExtensionId))
            .ToArray();
        if (removedSettings.Length != 0)
        {
            _dbContext.ExtensionSettings.RemoveRange(removedSettings);
            changed = true;
        }

        var removedExtensionRecords = extensionRecords
            .Where(value => !incomingExtensionIds.Contains(value.ExtensionId))
            .ToArray();
        var removedExtensionRecordIds = removedExtensionRecords.Select(static value => value.Id).ToHashSet();
        var removedNodeStates = extensionNodeStates
            .Where(value => removedExtensionRecordIds.Contains(value.ExtensionRecordId))
            .ToArray();
        if (removedNodeStates.Length != 0)
        {
            _dbContext.ExtensionNodeStates.RemoveRange(removedNodeStates);
            changed = true;
        }

        if (removedExtensionRecords.Length != 0)
        {
            _dbContext.ExtensionRecords.RemoveRange(removedExtensionRecords);
            changed = true;
        }

        var removedServices = services.Where(value => !incomingServiceIds.Contains(value.Id)).ToArray();
        if (removedServices.Length != 0)
        {
            _dbContext.Services.RemoveRange(removedServices);
            changed = true;
        }

        changed |= UpdateGlobalSettings(globalSettings, changes.GlobalSettings, now);

        foreach (var route in changes.Routes)
        {
            var entity = routes.FirstOrDefault(value => value.Id == route.Id);
            if (entity is null)
            {
                entity = ToRouteEntity(route, now);
                _dbContext.Routes.Add(entity);
                changed = true;
            }
            else
            {
                changed |= UpdateRoute(entity, route, now);
            }

            if (ownerExtensionId is not null && ownedRouteIds?.Contains(route.Id) == true &&
                !string.Equals(entity.OwnerExtensionId, ownerExtensionId, StringComparison.Ordinal))
            {
                entity.OwnerExtensionId = ownerExtensionId;
                changed = true;
            }
        }

        foreach (var service in changes.Services)
        {
            var entity = services.FirstOrDefault(value => value.Id == service.Id);
            if (entity is null)
            {
                entity = ToServiceEntity(service, now);
                _dbContext.Services.Add(entity);
                changed = true;
            }
            else
            {
                changed |= UpdateService(entity, service, now);
            }

            if (ownerExtensionId is not null && ownedServiceIds?.Contains(service.Id) == true &&
                !string.Equals(entity.OwnerExtensionId, ownerExtensionId, StringComparison.Ordinal))
            {
                entity.OwnerExtensionId = ownerExtensionId;
                changed = true;
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
                changed = true;
            }
            else
            {
                changed |= UpdateExtensionRecord(entity, extension, now);
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
                changed = true;
            }
            else
            {
                changed |= UpdateExtensionSetting(entity, setting, now);
            }
        }

        if (changed)
        {
            revision.CommittedAt = now;
            revision.UpdatedAt = now;
            revision.CommittedBy = EfHostConfigRevisionHelper.Committer;
        }

        return changed;
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

    private static bool UpdateRoute(Route entity, RouteConfiguration value, DateTimeOffset now) =>
        UpdateRoute(entity, value, now, true);

    private static bool UpdateRoute(
        Route entity,
        RouteConfiguration value,
        DateTimeOffset now,
        bool incrementVersion)
    {
        var matcherType = (RouteMatcherKind)value.Matcher.Type;
        var hostPatternsJson = HostConfigurationSemanticValidator.SerializeJson(value.Matcher.HostPatterns);
        var methodsJson = HostConfigurationSemanticValidator.SerializeJson(value.Matcher.Methods);
        var forwardingMode = (ForwardingKind)value.Forwarding.Mode;
        var replaceTemplate = value.Forwarding.ReplaceTemplate;
        var requestHeaderRewritesJson = HostConfigurationSemanticValidator.SerializeJson(value.RequestHeaderRewrites);
        var responseHeaderRewritesJson = HostConfigurationSemanticValidator.SerializeJson(value.ResponseHeaderRewrites);
        var metadataJson = HostConfigurationSemanticValidator.NormalizeJson(value.MetadataJson, JsonValueKind.Object);
        var ratePolicy = RatePolicyPersistenceMapper.ToPersistence(value.ClientIpRatePolicy);
        int? requestReadTimeoutMilliseconds = value.RequestReadTimeout is { } requestReadTimeout
            ? checked((int)requestReadTimeout.TotalMilliseconds)
            : null;
        var retryPolicy = ProxyRetryPersistenceMapper.ToNullablePersistence(value.ProxyRetries);

        RouteTargetKind targetType;
        string targetId;
        Guid? serviceId;
        string? staticRootPath;
        string? extensionHandlerId;
        switch (value.Target)
        {
            case MicroserviceRouteTargetConfiguration microservice:
                targetType = RouteTargetKind.Microservice;
                targetId = microservice.ServiceId.ToString("D");
                serviceId = microservice.ServiceId;
                staticRootPath = null;
                extensionHandlerId = null;
                break;
            case StaticFileRouteTargetConfiguration staticFile:
                targetType = RouteTargetKind.StaticFile;
                targetId = staticFile.RootPath;
                serviceId = null;
                staticRootPath = staticFile.RootPath;
                extensionHandlerId = null;
                break;
            case ExtensionHandlerRouteTargetConfiguration handler:
                targetType = RouteTargetKind.ExtensionHandler;
                targetId = handler.HandlerId;
                serviceId = null;
                staticRootPath = null;
                extensionHandlerId = handler.HandlerId;
                break;
            default:
                throw new HostConfigurationSemanticValidator.ConfigurationValidationException();
        }

        var changed = entity.Enabled != value.Enabled ||
            entity.MatcherType != matcherType ||
            !string.Equals(entity.Pattern, value.Matcher.Pattern, StringComparison.Ordinal) ||
            !string.Equals(entity.HostPatternsJson, hostPatternsJson, StringComparison.Ordinal) ||
            !string.Equals(entity.MethodsJson, methodsJson, StringComparison.Ordinal) ||
            entity.TargetType != targetType ||
            !string.Equals(entity.TargetId, targetId, StringComparison.Ordinal) ||
            entity.ServiceId != serviceId ||
            !string.Equals(entity.StaticRootPath, staticRootPath, StringComparison.Ordinal) ||
            !string.Equals(entity.ExtensionHandlerId, extensionHandlerId, StringComparison.Ordinal) ||
            entity.Priority != value.Priority ||
            entity.ForwardingMode != forwardingMode ||
            !string.Equals(entity.ReplaceTemplate, replaceTemplate, StringComparison.Ordinal) ||
            !string.Equals(entity.RequestHeaderRewritesJson, requestHeaderRewritesJson, StringComparison.Ordinal) ||
            !string.Equals(entity.ResponseHeaderRewritesJson, responseHeaderRewritesJson, StringComparison.Ordinal) ||
            !string.Equals(entity.MetadataJson, metadataJson, StringComparison.Ordinal) ||
            entity.ClientIpRateTokenLimit != ratePolicy.TokenLimit ||
            entity.ClientIpRateTokensPerPeriod != ratePolicy.TokensPerPeriod ||
            entity.ClientIpRateReplenishmentPeriodMilliseconds != ratePolicy.ReplenishmentPeriodMilliseconds ||
            entity.ClientIpRateQueueLimit != ratePolicy.QueueLimit ||
            entity.ClientIpRateRejectionBehavior != ratePolicy.RejectionBehavior ||
            entity.ClientIpRateRetryAfterBehavior != ratePolicy.RetryAfterBehavior ||
            entity.MaxRequestBodyBytes != value.MaxRequestBodyBytes ||
            entity.MaxRequestHeaderBytes != value.MaxRequestHeaderBytes ||
            entity.MaxConcurrentRequests != value.MaxConcurrentRequests ||
            entity.RequestReadTimeoutMilliseconds != requestReadTimeoutMilliseconds ||
            entity.ProxyMaxRetries != retryPolicy.MaxRetries ||
            entity.ProxyInitialRetryBackoffMilliseconds != retryPolicy.InitialBackoffMilliseconds ||
            entity.ProxyMaximumRetryBackoffMilliseconds != retryPolicy.MaximumBackoffMilliseconds ||
            entity.ProxyRetryOnConnectionFailure != retryPolicy.RetryOnConnectionFailure ||
            entity.ProxyRetryOnUpstreamDisconnect != retryPolicy.RetryOnUpstreamDisconnect;
        if (!changed && incrementVersion)
        {
            return false;
        }

        entity.Enabled = value.Enabled;
        entity.MatcherType = matcherType;
        entity.Pattern = value.Matcher.Pattern;
        entity.HostPatternsJson = hostPatternsJson;
        entity.MethodsJson = methodsJson;
        entity.TargetType = targetType;
        entity.TargetId = targetId;
        entity.ServiceId = serviceId;
        entity.StaticRootPath = staticRootPath;
        entity.ExtensionHandlerId = extensionHandlerId;
        entity.Priority = value.Priority;
        entity.ForwardingMode = forwardingMode;
        entity.ReplaceTemplate = replaceTemplate;
        entity.RequestHeaderRewritesJson = requestHeaderRewritesJson;
        entity.ResponseHeaderRewritesJson = responseHeaderRewritesJson;
        entity.MetadataJson = metadataJson;
        entity.ClientIpRateTokenLimit = ratePolicy.TokenLimit;
        entity.ClientIpRateTokensPerPeriod = ratePolicy.TokensPerPeriod;
        entity.ClientIpRateReplenishmentPeriodMilliseconds = ratePolicy.ReplenishmentPeriodMilliseconds;
        entity.ClientIpRateQueueLimit = ratePolicy.QueueLimit;
        entity.ClientIpRateRejectionBehavior = ratePolicy.RejectionBehavior;
        entity.ClientIpRateRetryAfterBehavior = ratePolicy.RetryAfterBehavior;
        entity.MaxRequestBodyBytes = value.MaxRequestBodyBytes;
        entity.MaxRequestHeaderBytes = value.MaxRequestHeaderBytes;
        entity.MaxConcurrentRequests = value.MaxConcurrentRequests;
        entity.RequestReadTimeoutMilliseconds = requestReadTimeoutMilliseconds;
        entity.ProxyMaxRetries = retryPolicy.MaxRetries;
        entity.ProxyInitialRetryBackoffMilliseconds = retryPolicy.InitialBackoffMilliseconds;
        entity.ProxyMaximumRetryBackoffMilliseconds = retryPolicy.MaximumBackoffMilliseconds;
        entity.ProxyRetryOnConnectionFailure = retryPolicy.RetryOnConnectionFailure;
        entity.ProxyRetryOnUpstreamDisconnect = retryPolicy.RetryOnUpstreamDisconnect;
        entity.UpdatedAt = now;
        if (incrementVersion)
        {
            entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
        }

        return true;
    }

    private static Service ToServiceEntity(ServiceConfiguration value, DateTimeOffset now)
    {
        var entity = new Service { Id = value.Id, Version = 1, CreatedAt = now, UpdatedAt = now };
        UpdateService(entity, value, now, false);
        return entity;
    }

    private static bool UpdateService(Service entity, ServiceConfiguration value, DateTimeOffset now) =>
        UpdateService(entity, value, now, true);

    private static bool UpdateService(
        Service entity,
        ServiceConfiguration value,
        DateTimeOffset now,
        bool incrementVersion)
    {
        var argumentListJson = HostConfigurationSemanticValidator.SerializeJson(value.ArgumentList);
        var environmentJson = HostConfigurationSemanticValidator.SerializeEnvironment(value.Environment);
        var startMode = (ServiceStartPolicy)value.StartMode;
        var restartPolicy = (DomainServiceRestartPolicy)value.RestartPolicy;
        var healthCheckType = (ServiceHealthCheckKind)value.HealthCheck.Type;
        var healthCheckTimeoutMilliseconds = checked((int)value.HealthCheck.Timeout.TotalMilliseconds);
        var changed = entity.Enabled != value.Enabled ||
            !string.Equals(entity.FileName, value.FileName, StringComparison.Ordinal) ||
            !string.Equals(entity.ArgumentListJson, argumentListJson, StringComparison.Ordinal) ||
            !string.Equals(entity.WorkingDirectory, value.WorkingDirectory, StringComparison.Ordinal) ||
            !string.Equals(entity.EnvironmentJson, environmentJson, StringComparison.Ordinal) ||
            entity.StartMode != startMode ||
            entity.RestartPolicy != restartPolicy ||
            entity.HealthCheckType != healthCheckType ||
            !string.Equals(entity.HealthCheckHttpPath, value.HealthCheck.HttpPath, StringComparison.Ordinal) ||
            entity.HealthCheckTimeoutMilliseconds != healthCheckTimeoutMilliseconds;
        if (!changed && incrementVersion)
        {
            return false;
        }

        entity.Enabled = value.Enabled;
        entity.FileName = value.FileName;
        entity.ArgumentListJson = argumentListJson;
        entity.WorkingDirectory = value.WorkingDirectory;
        entity.EnvironmentJson = environmentJson;
        entity.StartMode = startMode;
        entity.RestartPolicy = restartPolicy;
        entity.HealthCheckType = healthCheckType;
        entity.HealthCheckHttpPath = value.HealthCheck.HttpPath;
        entity.HealthCheckTimeoutMilliseconds = healthCheckTimeoutMilliseconds;
        entity.UpdatedAt = now;
        if (incrementVersion)
        {
            entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
        }

        return true;
    }

    private static ExtensionRecord ToExtensionRecordEntity(ExtensionRecordConfiguration value, DateTimeOffset now) =>
        new()
        {
            Id = EfHostConfigRevisionHelper.NewUuidV7(),
            ExtensionId = value.ExtensionId,
            InstalledVersion = value.Version,
            ContentHash = value.ContentHash,
            LoadState = (DomainExtensionLoadState)value.LoadState,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

    private static bool UpdateExtensionRecord(
        ExtensionRecord entity,
        ExtensionRecordConfiguration value,
        DateTimeOffset now)
    {
        if (string.Equals(entity.InstalledVersion, value.Version, StringComparison.Ordinal) &&
            string.Equals(entity.ContentHash, value.ContentHash, StringComparison.OrdinalIgnoreCase) &&
            entity.LoadState == (DomainExtensionLoadState)value.LoadState)
        {
            return false;
        }

        entity.InstalledVersion = value.Version;
        entity.ContentHash = value.ContentHash;
        entity.LoadState = (DomainExtensionLoadState)value.LoadState;
        entity.UpdatedAt = now;
        entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
        return true;
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

    private static bool UpdateExtensionSetting(
        ExtensionSetting entity,
        ExtensionSettingsConfiguration value,
        DateTimeOffset now)
    {
        var settingsJson = HostConfigurationSemanticValidator.NormalizeJson(value.SettingsJson, null);
        if (entity.SchemaVersion == value.SchemaVersion &&
            string.Equals(entity.SettingsJson, settingsJson, StringComparison.Ordinal))
        {
            return false;
        }

        entity.SchemaVersion = value.SchemaVersion;
        entity.SettingsJson = settingsJson;
        entity.UpdatedAt = now;
        entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
        return true;
    }

    private static bool UpdateGlobalSettings(
        GlobalSettings entity,
        GlobalSettingsConfiguration value,
        DateTimeOffset now)
    {
        var configurationPollIntervalSeconds = checked((int)value.ConfigurationPollInterval.TotalSeconds);
        var requestReadTimeoutMilliseconds = checked((int)value.RequestReadTimeout.TotalMilliseconds);
        var trustedProxyCidrsJson = HostConfigurationSemanticValidator.SerializeJson(value.TrustedProxyCidrs);
        var connectTimeoutMilliseconds = checked((int)value.ProxyTimeouts.ConnectTimeout.TotalMilliseconds);
        var httpActivityTimeoutMilliseconds = checked((int)value.ProxyTimeouts.HttpActivityTimeout.TotalMilliseconds);
        var httpTotalTimeoutMilliseconds = checked((int)value.ProxyTimeouts.HttpTotalTimeout.TotalMilliseconds);
        var webSocketIdleTimeoutMilliseconds = checked((int)value.ProxyTimeouts.WebSocketIdleTimeout.TotalMilliseconds);
        var ratePolicy = RatePolicyPersistenceMapper.ToPersistence(value.ClientIpRatePolicy);
        var retryPolicy = ProxyRetryPersistenceMapper.ToPersistence(value.ProxyRetries);
        var changed = entity.AutoPortRangeStart != value.AutoPortRangeStart ||
            entity.AutoPortRangeEnd != value.AutoPortRangeEnd ||
            entity.MaxRequestBodyBytes != value.MaxRequestBodyBytes ||
            entity.MaxRequestHeaderBytes != value.MaxRequestHeaderBytes ||
            entity.MaxConcurrentRequests != value.MaxConcurrentRequests ||
            entity.ConfigurationPollIntervalSeconds != configurationPollIntervalSeconds ||
            entity.RequestReadTimeoutMilliseconds != requestReadTimeoutMilliseconds ||
            !string.Equals(entity.TrustedProxyCidrsJson, trustedProxyCidrsJson, StringComparison.Ordinal) ||
            entity.ConnectTimeoutMilliseconds != connectTimeoutMilliseconds ||
            entity.HttpActivityTimeoutMilliseconds != httpActivityTimeoutMilliseconds ||
            entity.HttpTotalTimeoutMilliseconds != httpTotalTimeoutMilliseconds ||
            entity.WebSocketIdleTimeoutMilliseconds != webSocketIdleTimeoutMilliseconds ||
            entity.ClientIpRateTokenLimit != ratePolicy.TokenLimit ||
            entity.ClientIpRateTokensPerPeriod != ratePolicy.TokensPerPeriod ||
            entity.ClientIpRateReplenishmentPeriodMilliseconds != ratePolicy.ReplenishmentPeriodMilliseconds ||
            entity.ClientIpRateQueueLimit != ratePolicy.QueueLimit ||
            entity.ClientIpRateRejectionBehavior != ratePolicy.RejectionBehavior ||
            entity.ClientIpRateRetryAfterBehavior != ratePolicy.RetryAfterBehavior ||
            entity.ProxyMaxRetries != retryPolicy.MaxRetries ||
            entity.ProxyInitialRetryBackoffMilliseconds != retryPolicy.InitialBackoffMilliseconds ||
            entity.ProxyMaximumRetryBackoffMilliseconds != retryPolicy.MaximumBackoffMilliseconds ||
            entity.ProxyRetryOnConnectionFailure != retryPolicy.RetryOnConnectionFailure ||
            entity.ProxyRetryOnUpstreamDisconnect != retryPolicy.RetryOnUpstreamDisconnect;
        if (!changed)
        {
            return false;
        }

        entity.AutoPortRangeStart = value.AutoPortRangeStart;
        entity.AutoPortRangeEnd = value.AutoPortRangeEnd;
        entity.MaxRequestBodyBytes = value.MaxRequestBodyBytes;
        entity.MaxRequestHeaderBytes = value.MaxRequestHeaderBytes;
        entity.MaxConcurrentRequests = value.MaxConcurrentRequests;
        entity.ConfigurationPollIntervalSeconds = configurationPollIntervalSeconds;
        entity.RequestReadTimeoutMilliseconds = requestReadTimeoutMilliseconds;
        entity.TrustedProxyCidrsJson = trustedProxyCidrsJson;
        entity.ConnectTimeoutMilliseconds = connectTimeoutMilliseconds;
        entity.HttpActivityTimeoutMilliseconds = httpActivityTimeoutMilliseconds;
        entity.HttpTotalTimeoutMilliseconds = httpTotalTimeoutMilliseconds;
        entity.WebSocketIdleTimeoutMilliseconds = webSocketIdleTimeoutMilliseconds;
        entity.ClientIpRateTokenLimit = ratePolicy.TokenLimit;
        entity.ClientIpRateTokensPerPeriod = ratePolicy.TokensPerPeriod;
        entity.ClientIpRateReplenishmentPeriodMilliseconds = ratePolicy.ReplenishmentPeriodMilliseconds;
        entity.ClientIpRateQueueLimit = ratePolicy.QueueLimit;
        entity.ClientIpRateRejectionBehavior = ratePolicy.RejectionBehavior;
        entity.ClientIpRateRetryAfterBehavior = ratePolicy.RetryAfterBehavior;
        entity.ProxyMaxRetries = retryPolicy.MaxRetries;
        entity.ProxyInitialRetryBackoffMilliseconds = retryPolicy.InitialBackoffMilliseconds;
        entity.ProxyMaximumRetryBackoffMilliseconds = retryPolicy.MaximumBackoffMilliseconds;
        entity.ProxyRetryOnConnectionFailure = retryPolicy.RetryOnConnectionFailure;
        entity.ProxyRetryOnUpstreamDisconnect = retryPolicy.RetryOnUpstreamDisconnect;
        entity.UpdatedAt = now;
        entity.Version = EfHostConfigRevisionHelper.IncrementVersion(entity.Version);
        return true;
    }
}
