using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Implements the owner-bound configuration seam without exposing EF types.</summary>
public sealed class EfExtensionOwnedConfigurationApi : IExtensionOwnedConfigurationApi
{
    private readonly EfHostConfigApi _host;

    /// <summary>Creates the scoped extension configuration store.</summary>
    public EfExtensionOwnedConfigurationApi(EfHostConfigApi host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc />
    public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadOwnedAsync(
        string extensionId,
        CancellationToken cancellationToken = default) =>
        _host.ReadExtensionOwnedAsync(extensionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadOwnedSettingsAsync(
        string extensionId,
        CancellationToken cancellationToken = default) =>
        _host.ReadExtensionSettingsAsync(extensionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConfigurationWriteResult> WriteOwnedSettingsAsync(
        string extensionId,
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default)
    {
        if (settings is null || !string.Equals(extensionId, settings.ExtensionId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation)));
        }

        return _host.WriteExtensionSettingsAsync(extensionId, expectedVersion, settings, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> ApplyOwnedAsync(
        string extensionId,
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        Func<string, bool>? handlerIsOwned = null,
        CancellationToken cancellationToken = default)
    {
        if (!HostConfigurationSemanticValidator.IsSafeExtensionId(extensionId) || changes is null)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        var fullResult = await _host.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!fullResult.IsSuccess || fullResult.Value is not { } full)
        {
            return ConfigurationWriteResult.Failure(fullResult.Errors.ToArray());
        }

        var ownedResult = await _host.ReadExtensionOwnedAsync(extensionId, cancellationToken).ConfigureAwait(false);
        if (!ownedResult.IsSuccess || ownedResult.Value is not { } owned)
        {
            return ConfigurationWriteResult.Failure(ownedResult.Errors.ToArray());
        }

        var ownedRouteIds = owned.Routes.Select(value => value.Id).ToHashSet();
        var ownedServiceIds = owned.Services.Select(value => value.Id).ToHashSet();
        var knownRouteIds = full.Routes.Select(value => value.Id).ToHashSet();
        var knownServiceIds = full.Services.Select(value => value.Id).ToHashSet();
        var serviceUpsertIds = changes.ServiceUpserts.Select(value => value.Id).ToHashSet();
        foreach (var service in changes.ServiceUpserts)
        {
            if ((knownServiceIds.Contains(service.Id) && !ownedServiceIds.Contains(service.Id)) ||
                (!ownedServiceIds.Contains(service.Id) && service.Version != 0))
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));
            }
        }

        foreach (var route in changes.Upserts)
        {
            if (knownRouteIds.Contains(route.Id) && !ownedRouteIds.Contains(route.Id))
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));
            }
        }

        foreach (var route in changes.Upserts)
        {
            if (route.Target is ExtensionHandlerRouteTarget handler &&
                !(handlerIsOwned?.Invoke(handler.HandlerId) ?? false))
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));
            }

            if (route.Target is ExtensionServiceRouteTarget service &&
                !ownedServiceIds.Contains(service.ServiceId) && !serviceUpsertIds.Contains(service.ServiceId))
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound));
            }
        }

        if (changes.Settings is { } settings &&
            !string.Equals(settings.ExtensionId, extensionId, StringComparison.Ordinal))
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        if (changes.RemovedRouteIds.Any(routeId => !ownedRouteIds.Contains(routeId)))
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound));
        }

        if (changes.RemovedServiceIds.Any(serviceId => !ownedServiceIds.Contains(serviceId)))
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound));
        }

        var removedRoutes = changes.RemovedRouteIds.ToHashSet();
        var routes = full.Routes
            .Where(value => !removedRoutes.Contains(value.Id))
            .ToDictionary(value => value.Id);
        foreach (var route in changes.Upserts)
        {
            routes[route.Id] = ToHostRoute(route, routes.TryGetValue(route.Id, out var current) ? current : null);
        }

        var removedServices = changes.RemovedServiceIds.ToHashSet();
        var services = full.Services
            .Where(value => !removedServices.Contains(value.Id))
            .ToDictionary(value => value.Id);
        foreach (var service in changes.ServiceUpserts)
        {
            services[service.Id] = ToHostService(
                service,
                services.TryGetValue(service.Id, out var current) ? current : null);
        }
        var settingsById = full.ExtensionSettings
            .ToDictionary(value => value.ExtensionId, StringComparer.Ordinal);
        if (changes.Settings is null && owned.Settings is not null)
        {
            settingsById[extensionId] = owned.Settings;
        }
        else
        {
            settingsById.Remove(extensionId);
        }
        if (changes.Settings is { } replacement)
        {
            settingsById[extensionId] = replacement;
        }

        var hostChanges = new ConfigurationChangeSet(
            full.GlobalSettings,
            routes.Values.ToImmutableArray(),
            services.Values.ToImmutableArray(),
            full.ExtensionRecords,
            settingsById.Values.ToImmutableArray());
        ownedRouteIds.ExceptWith(removedRoutes);
        ownedRouteIds.UnionWith(changes.Upserts.Select(value => value.Id));
        ownedServiceIds.ExceptWith(removedServices);
        ownedServiceIds.UnionWith(changes.ServiceUpserts.Select(value => value.Id));

        return await _host.WriteExtensionOwnedSnapshotAsync(
            extensionId,
            expectedVersion,
            hostChanges,
            ownedRouteIds,
            ownedServiceIds,
            cancellationToken).ConfigureAwait(false);
    }

    private static RouteConfiguration ToHostRoute(
        ExtensionRouteConfiguration route,
        RouteConfiguration? current)
    {
        RouteTargetConfiguration target = route.Target switch
        {
            ExtensionServiceRouteTarget service => new MicroserviceRouteTargetConfiguration(service.ServiceId),
            ExtensionHandlerRouteTarget handler => new ExtensionHandlerRouteTargetConfiguration(handler.HandlerId),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        return new RouteConfiguration(
            route.Id,
            route.Enabled,
            route.Matcher,
            target,
            route.Priority,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            current?.CreatedAt ?? DateTimeOffset.UtcNow,
            current?.UpdatedAt ?? DateTimeOffset.UtcNow,
            current?.Version ?? 0);
    }

    private static ServiceConfiguration ToHostService(
        ExtensionServiceConfiguration service,
        ServiceConfiguration? current) =>
        new(
            service.Id,
            service.Enabled,
            service.FileName,
            service.ArgumentList,
            service.WorkingDirectory,
            current?.Environment ?? ImmutableDictionary<string, string>.Empty,
            service.StartMode,
            service.RestartPolicy,
            service.HealthCheck,
            current?.CreatedAt ?? service.CreatedAt,
            DateTimeOffset.UtcNow,
            service.Version);
}
