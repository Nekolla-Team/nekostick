using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;
using ContractExtensionLoadState = Nekolla.Nekostick.Contracts.ExtensionLoadState;
using ContractServiceRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Maps persisted host configuration entities into contract DTOs.</summary>
internal static class EfHostConfigDtoEntityMapper
{
    internal static HostConfigurationSnapshot MapSnapshot(
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
            HostConfigurationSemanticValidator.DeserializeStringArray(value.TrustedProxyCidrsJson),
            new ProxyTimeoutConfiguration(
                connectTimeout: TimeSpan.FromMilliseconds(value.ConnectTimeoutMilliseconds),
                httpActivityTimeout: TimeSpan.FromMilliseconds(value.HttpActivityTimeoutMilliseconds),
                httpTotalTimeout: TimeSpan.FromMilliseconds(value.HttpTotalTimeoutMilliseconds),
                webSocketIdleTimeout: TimeSpan.FromMilliseconds(value.WebSocketIdleTimeoutMilliseconds)),
            value.MaxRequestHeaderBytes,
            TimeSpan.FromMilliseconds(value.RequestReadTimeoutMilliseconds),
            RatePolicyPersistenceMapper.ToContract(
                value.ClientIpRateTokenLimit,
                value.ClientIpRateTokensPerPeriod,
                value.ClientIpRateReplenishmentPeriodMilliseconds,
                value.ClientIpRateQueueLimit,
                value.ClientIpRateRejectionBehavior,
                value.ClientIpRateRetryAfterBehavior),
            ProxyRetryPersistenceMapper.ToContract(
                value.ProxyMaxRetries,
                value.ProxyInitialRetryBackoffMilliseconds,
                value.ProxyMaximumRetryBackoffMilliseconds,
                value.ProxyRetryOnConnectionFailure,
                value.ProxyRetryOnUpstreamDisconnect));

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
            value.Version,
            RatePolicyPersistenceMapper.ToContract(
                value.ClientIpRateTokenLimit,
                value.ClientIpRateTokensPerPeriod,
                value.ClientIpRateReplenishmentPeriodMilliseconds,
                value.ClientIpRateQueueLimit,
                value.ClientIpRateRejectionBehavior,
                value.ClientIpRateRetryAfterBehavior),
            value.MaxRequestBodyBytes,
            value.MaxRequestHeaderBytes,
            value.MaxConcurrentRequests,
            value.RequestReadTimeoutMilliseconds is { } requestReadTimeoutMilliseconds
                ? TimeSpan.FromMilliseconds(requestReadTimeoutMilliseconds)
                : null,
            ProxyRetryPersistenceMapper.ToNullableContract(
                value.ProxyMaxRetries,
                value.ProxyInitialRetryBackoffMilliseconds,
                value.ProxyMaximumRetryBackoffMilliseconds,
                value.ProxyRetryOnConnectionFailure,
                value.ProxyRetryOnUpstreamDisconnect));
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
}
