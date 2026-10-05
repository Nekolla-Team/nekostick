using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationValidation
{
    internal static void ValidateConfigurationValues(
        GlobalSettingsConfiguration globalSettings,
        IEnumerable<RouteConfiguration> routes,
        IEnumerable<ServiceConfiguration> services,
        IEnumerable<ExtensionRecordConfiguration> extensionRecords,
        IEnumerable<ExtensionSettingsConfiguration> extensionSettings,
        ILogger? logger = null)
    {
        List<string>? environmentValidationMessages = null;
        ValidateConfigurationValuesCore(
            globalSettings,
            routes,
            services,
            extensionRecords,
            extensionSettings,
            logger,
            ref environmentValidationMessages,
            collectEnvironmentViolations: false);
    }

    internal static void ValidateConfigurationValuesForChangeSet(
        GlobalSettingsConfiguration globalSettings,
        IEnumerable<RouteConfiguration> routes,
        IEnumerable<ServiceConfiguration> services,
        IEnumerable<ExtensionRecordConfiguration> extensionRecords,
        IEnumerable<ExtensionSettingsConfiguration> extensionSettings,
        ILogger? logger,
        ref List<string>? environmentValidationMessages) =>
        ValidateConfigurationValuesCore(
            globalSettings,
            routes,
            services,
            extensionRecords,
            extensionSettings,
            logger,
            ref environmentValidationMessages,
            collectEnvironmentViolations: true);

    private static void ValidateConfigurationValuesCore(
        GlobalSettingsConfiguration globalSettings,
        IEnumerable<RouteConfiguration> routes,
        IEnumerable<ServiceConfiguration> services,
        IEnumerable<ExtensionRecordConfiguration> extensionRecords,
        IEnumerable<ExtensionSettingsConfiguration> extensionSettings,
        ILogger? logger,
        ref List<string>? environmentValidationMessages,
        bool collectEnvironmentViolations)
    {
        if (globalSettings is null)
        {
            HostConfigurationValueValidator.Throw();
        }

        HostConfigurationGlobalValidator.Validate(globalSettings);
        var routeArray = routes?.ToArray() ?? Throw<RouteConfiguration[]>();
        var serviceArray = services?.ToArray() ?? Throw<ServiceConfiguration[]>();
        var extensionArray = extensionRecords?.ToArray() ?? Throw<ExtensionRecordConfiguration[]>();
        var settingsArray = extensionSettings?.ToArray() ?? Throw<ExtensionSettingsConfiguration[]>();

        HostConfigurationValueValidator.ValidateUniqueIds(routeArray.Select(value => value?.Id ?? Guid.Empty), true);
        HostConfigurationValueValidator.ValidateUniqueIds(serviceArray.Select(value => value?.Id ?? Guid.Empty), true);
        HostConfigurationValueValidator.ValidateUniqueText(
            extensionArray.Select(value => value?.ExtensionId),
            HostConfigurationValueValidator.MaxExtensionIdLength);
        HostConfigurationValueValidator.ValidateUniqueText(
            settingsArray.Select(value => value?.ExtensionId),
            HostConfigurationValueValidator.MaxExtensionIdLength);

        foreach (var service in serviceArray)
        {
            if (collectEnvironmentViolations)
            {
                HostConfigurationServiceValidator.ValidateForChangeSet(service, ref environmentValidationMessages);
            }
            else
            {
                HostConfigurationServiceValidator.Validate(service);
            }
        }

        foreach (var extension in extensionArray)
        {
            HostConfigurationExtensionValidator.ValidateRecord(extension);
        }

        var serviceIds = serviceArray.Select(value => value.Id).ToHashSet();
        var extensionIds = extensionArray
            .Select(value => value.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var route in routeArray)
        {
            HostConfigurationRouteValidator.Validate(route, globalSettings, serviceIds, logger);
        }

        foreach (var setting in settingsArray)
        {
            HostConfigurationExtensionValidator.ValidateSettings(setting);
            if (!extensionIds.Contains(setting.ExtensionId))
            {
                HostConfigurationValueValidator.Throw();
            }
        }
    }

    private static T Throw<T>()
    {
        HostConfigurationValueValidator.Throw();
        return default!;
    }
}
