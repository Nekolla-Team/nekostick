using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationGlobalValidator
{
    internal static void Validate(GlobalSettingsConfiguration value)
    {
        if (value.Version < 0 || value.AutoPortRangeStart is < 1 or > 65535 ||
            value.AutoPortRangeEnd is < 1 or > 65535 ||
            value.AutoPortRangeStart > value.AutoPortRangeEnd ||
            value.MaxRequestBodyBytes is <= 0 or > GlobalSettingsConfiguration.HardMaximumRequestBodyBytes ||
            value.MaxRequestHeaderBytes is <= 0 or > GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes ||
            value.MaxConcurrentRequests <= 0 ||
            value.ConfigurationPollInterval <= TimeSpan.Zero ||
            value.ConfigurationPollInterval.Ticks % TimeSpan.TicksPerSecond != 0 ||
            value.ConfigurationPollInterval.TotalSeconds > int.MaxValue ||
            value.RequestReadTimeout <= TimeSpan.Zero ||
            value.RequestReadTimeout.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            value.RequestReadTimeout > TimeSpan.FromDays(1) ||
            !ProxyTimeoutPersistenceDefaults.IsValidTimeout(value.ProxyTimeouts.ConnectTimeout) ||
            !ProxyTimeoutPersistenceDefaults.IsValidTimeout(value.ProxyTimeouts.HttpActivityTimeout) ||
            !ProxyTimeoutPersistenceDefaults.IsValidTimeout(value.ProxyTimeouts.HttpTotalTimeout) ||
            !ProxyTimeoutPersistenceDefaults.IsValidTimeout(value.ProxyTimeouts.WebSocketIdleTimeout) ||
            !ProxyRetryPersistenceDefaults.IsValidRetryPolicy(value.ProxyRetries))
        {
            HostConfigurationValueValidator.Throw();
        }

        HostConfigurationRatePolicyValidator.Validate(value.ClientIpRatePolicy);
        var cidrs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cidr in value.TrustedProxyCidrs)
        {
            if (!HostConfigurationValueValidator.IsValidCidr(cidr) || !cidrs.Add(cidr))
            {
                HostConfigurationValueValidator.Throw();
            }
        }

        HostConfigurationValueValidator.EnsureSerializedJson(value.TrustedProxyCidrs, JsonValueKind.Array);
    }


    internal static void ValidatePersistedVersions(HostConfigurationSnapshot snapshot)
    {
        if (snapshot.Version < 1 || snapshot.GlobalSettings.Version < 1 ||
            snapshot.Routes.Any(value => value.Version < 1) ||
            snapshot.Services.Any(value => value.Version < 1) ||
            snapshot.ExtensionRecords.Any(value => value.RecordVersion < 1) ||
            snapshot.ExtensionSettings.Any(value => value.Version < 1))
        {
            HostConfigurationValueValidator.Throw();
        }
    }
}
