using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationValidation
{
    internal static void ValidateConfigurationValues(
        GlobalSettingsConfiguration globalSettings,
        IEnumerable<RouteConfiguration> routes,
        IEnumerable<ServiceConfiguration> services,
        IEnumerable<ExtensionRecordConfiguration> extensionRecords,
        IEnumerable<ExtensionSettingsConfiguration> extensionSettings)
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
            HostConfigurationServiceValidator.Validate(service);
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
            HostConfigurationRouteValidator.Validate(route, globalSettings, serviceIds, extensionIds);
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
