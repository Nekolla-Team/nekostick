using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using PersistenceRoute = Nekolla.Nekostick.Persistence.Entities.Route;

namespace Nekolla.Nekostick.Host;

/// <summary>Loads the complete business configuration without publishing partial query results.</summary>
public interface IHostConfigurationSnapshotReader
{
    /// <summary>Reads and validates a complete configuration snapshot.</summary>
    Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadCompleteAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Reads all configuration tables through a short-lived EF context.</summary>
public sealed class EfHostConfigurationSnapshotReader : IHostConfigurationSnapshotReader
{
    private readonly IDbContextFactory<NekostickDbContext> _dbContextFactory;

    /// <summary>Creates a complete snapshot reader.</summary>
    public EfHostConfigurationSnapshotReader(IDbContextFactory<NekostickDbContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));

    /// <inheritdoc />
    public async Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadCompleteAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                cancellationToken);
            var revision = await dbContext.ConfigurationRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            var globalSettings = await dbContext.GlobalSettings
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            var services = await dbContext.Services.AsNoTracking().ToArrayAsync(cancellationToken);
            var routes = await dbContext.Routes.AsNoTracking().ToArrayAsync(cancellationToken);
            var extensionRecords = await dbContext.ExtensionRecords.AsNoTracking().ToArrayAsync(cancellationToken);
            var extensionSettings = await dbContext.ExtensionSettings.AsNoTracking().ToArrayAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (revision is null || globalSettings is null)
            {
                return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.NotFound));
            }

            try
            {
                var snapshot = HostConfigurationSnapshotMapper.Map(
                    revision,
                    globalSettings,
                    routes,
                    services,
                    extensionRecords,
                    extensionSettings);
                return HostConfigurationSnapshotValidator.IsComplete(snapshot) &&
                    HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot)
                    ? ConfigurationReadResult<HostConfigurationSnapshot>.Success(snapshot)
                    : ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                        new ConfigurationError(ConfigurationErrorCode.Validation));
            }
            catch (Exception)
            {
                return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
        }
    }

}

/// <summary>Maps persistence entities into immutable contract DTOs.</summary>
internal static class HostConfigurationSnapshotMapper
{
    internal static HostConfigurationSnapshot Map(
        ConfigurationRevision revision,
        GlobalSettings globalSettings,
        IEnumerable<PersistenceRoute> routeRows,
        IEnumerable<Service> serviceRows,
        IEnumerable<ExtensionRecord> extensionRecordRows,
        IEnumerable<ExtensionSetting> extensionSettingRows)
    {
        var extensionRecords = extensionRecordRows
            .Select(MapExtensionRecord)
            .ToImmutableArray();
        var extensionIdsByRecordId = extensionRecordRows
            .ToDictionary(value => value.Id, value => value.ExtensionId);

        var extensionSettings = extensionSettingRows
            .Select(value =>
            {
                if (!extensionIdsByRecordId.TryGetValue(value.ExtensionRecordId, out var extensionId))
                {
                    throw new InvalidDataException("An extension setting references an unknown extension record.");
                }

                return new ExtensionSettingsConfiguration(
                    extensionId,
                    value.SchemaVersion,
                    RequireJson(value.SettingsJson),
                    value.Version);
            })
            .ToImmutableArray();

        var services = serviceRows.Select(MapService).ToImmutableArray();
        var routes = routeRows.Select(MapRoute).ToImmutableArray();
        var global = new GlobalSettingsConfiguration(
            globalSettings.Version,
            globalSettings.AutoPortRangeStart,
            globalSettings.AutoPortRangeEnd,
            globalSettings.MaxRequestBodyBytes,
            globalSettings.MaxConcurrentRequests,
            TimeSpan.FromSeconds(globalSettings.ConfigurationPollIntervalSeconds),
            ReadStringArray(globalSettings.TrustedProxyCidrsJson),
            new ProxyTimeoutConfiguration(
                connectTimeout: TimeSpan.FromMilliseconds(globalSettings.ConnectTimeoutMilliseconds),
                httpActivityTimeout: TimeSpan.FromMilliseconds(globalSettings.HttpActivityTimeoutMilliseconds),
                httpTotalTimeout: TimeSpan.FromMilliseconds(globalSettings.HttpTotalTimeoutMilliseconds),
                webSocketIdleTimeout: TimeSpan.FromMilliseconds(globalSettings.WebSocketIdleTimeoutMilliseconds)),
            globalSettings.MaxRequestHeaderBytes,
            TimeSpan.FromMilliseconds(globalSettings.RequestReadTimeoutMilliseconds),
            RatePolicyPersistenceMapper.ToContract(
                globalSettings.ClientIpRateTokenLimit,
                globalSettings.ClientIpRateTokensPerPeriod,
                globalSettings.ClientIpRateReplenishmentPeriodMilliseconds,
                globalSettings.ClientIpRateQueueLimit,
                globalSettings.ClientIpRateRejectionBehavior,
                globalSettings.ClientIpRateRetryAfterBehavior),
            ProxyRetryPersistenceMapper.ToContract(
                globalSettings.ProxyMaxRetries,
                globalSettings.ProxyInitialRetryBackoffMilliseconds,
                globalSettings.ProxyMaximumRetryBackoffMilliseconds,
                globalSettings.ProxyRetryOnConnectionFailure,
                globalSettings.ProxyRetryOnUpstreamDisconnect));

        return new HostConfigurationSnapshot(
            revision.Version,
            global,
            routes,
            services,
            extensionRecords,
            extensionSettings);
    }

    private static ExtensionRecordConfiguration MapExtensionRecord(ExtensionRecord value) =>
        new(
            value.ExtensionId,
            value.InstalledVersion,
            (Nekolla.Nekostick.Contracts.ExtensionLoadState)value.LoadState,
            value.CreatedAt,
            value.UpdatedAt,
            value.Version);

    private static ServiceConfiguration MapService(Service value) =>
        new(
            value.Id,
            value.Enabled,
            value.FileName,
            ReadStringArray(value.ArgumentListJson),
            value.WorkingDirectory,
            ReadStringDictionary(value.EnvironmentJson),
            (ServiceStartMode)value.StartMode,
            (Nekolla.Nekostick.Contracts.ServiceRestartPolicy)value.RestartPolicy,
            new ServiceHealthCheckConfiguration(
                (ServiceHealthCheckType)value.HealthCheckType,
                value.HealthCheckHttpPath,
                TimeSpan.FromMilliseconds(value.HealthCheckTimeoutMilliseconds)),
            value.CreatedAt,
            value.UpdatedAt,
            value.Version);

    private static RouteConfiguration MapRoute(PersistenceRoute value)
    {
        RouteTargetConfiguration target = value.TargetType switch
        {
            RouteTargetKind.Microservice when value.ServiceId is { } serviceId &&
                string.Equals(value.TargetId, serviceId.ToString(), StringComparison.OrdinalIgnoreCase) =>
                new MicroserviceRouteTargetConfiguration(serviceId),
            RouteTargetKind.StaticFile when value.StaticRootPath is not null &&
                string.Equals(value.TargetId, value.StaticRootPath, StringComparison.Ordinal) =>
                new StaticFileRouteTargetConfiguration(value.StaticRootPath),
            RouteTargetKind.ExtensionHandler when !string.IsNullOrWhiteSpace(value.ExtensionHandlerId) &&
                string.Equals(value.TargetId, value.ExtensionHandlerId, StringComparison.Ordinal) =>
                new ExtensionHandlerRouteTargetConfiguration(value.ExtensionHandlerId),
            _ => throw new InvalidDataException("A route target is incomplete.")
        };

        return new RouteConfiguration(
            value.Id,
            value.Enabled,
            new RouteMatcherConfiguration(
                (RouteMatcherType)value.MatcherType,
                value.Pattern,
                ReadStringArray(value.HostPatternsJson),
                ReadStringArray(value.MethodsJson)),
            target,
            value.Priority,
            new ForwardingConfiguration(
                (ForwardingMode)value.ForwardingMode,
                value.ReplaceTemplate),
            ReadHeaderRewrites(value.RequestHeaderRewritesJson),
            ReadHeaderRewrites(value.ResponseHeaderRewritesJson),
            RequireJsonObject(value.MetadataJson),
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

    private static ImmutableArray<string> ReadStringArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A JSON array was expected.");
        }

        var values = ImmutableArray.CreateBuilder<string>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("A JSON string array was expected.");
            }

            values.Add(element.GetString() ?? throw new InvalidDataException("A JSON string was null."));
        }

        return values.ToImmutable();
    }

    private static ImmutableDictionary<string, string> ReadStringDictionary(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A JSON object was expected.");
        }

        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                !values.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
            {
                throw new InvalidDataException("A JSON string object was expected.");
            }
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<HeaderRewriteConfiguration> ReadHeaderRewrites(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A JSON rewrite array was expected.");
        }

        var rewrites = ImmutableArray.CreateBuilder<HeaderRewriteConfiguration>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("operation", out var operationElement) ||
                !element.TryGetProperty("name", out var nameElement) ||
                operationElement.ValueKind != JsonValueKind.String ||
                nameElement.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<HeaderRewriteOperation>(operationElement.GetString(), true, out var operation))
            {
                throw new InvalidDataException("A header rewrite is incomplete.");
            }

            string? value = null;
            if (element.TryGetProperty("value", out var valueElement) && valueElement.ValueKind != JsonValueKind.Null)
            {
                value = valueElement.GetString() ?? throw new InvalidDataException("A rewrite value was invalid.");
            }

            rewrites.Add(new HeaderRewriteConfiguration(operation, nameElement.GetString()!, value));
        }

        return rewrites.ToImmutable();
    }

    private static string RequireJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? throw new InvalidDataException("A JSON document is required.")
            : json;
    }

    private static string RequireJsonObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? json
            : throw new InvalidDataException("A JSON object is required.");
    }
}
