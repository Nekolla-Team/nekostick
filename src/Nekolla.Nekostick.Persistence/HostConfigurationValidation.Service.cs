using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationServiceValidator
{
    internal static void Validate(ServiceConfiguration? value)
    {
        if (value is null || !HostConfigurationValueValidator.IsUuidV7(value.Id) || value.Version < 0 ||
            !HostConfigurationValueValidator.IsSafeServicePath(
                value.FileName,
                HostConfigurationValueValidator.MaxTextLength) ||
            !HostConfigurationValueValidator.IsSafeServicePath(
                value.WorkingDirectory,
                HostConfigurationValueValidator.MaxTextLength) ||
            !Enum.IsDefined(value.StartMode) || !Enum.IsDefined(value.RestartPolicy) ||
            value.ArgumentList.IsDefault || value.Environment is null || value.HealthCheck is null)
        {
            HostConfigurationValueValidator.Throw();
        }

        foreach (var argument in value.ArgumentList)
        {
            if (argument is null || argument.Length > HostConfigurationValueValidator.MaxArgumentLength ||
                ContainsControlCharacter(argument))
            {
                HostConfigurationValueValidator.Throw();
            }
        }

        foreach (var pair in value.Environment)
        {
            if (!HostConfigurationValueValidator.IsSafeEnvironmentKey(pair.Key) ||
                !HostConfigurationValueValidator.IsSafeEnvironmentValue(pair.Value))
            {
                HostConfigurationValueValidator.Throw();
            }
        }

        HostConfigurationValueValidator.EnsureSerializedJson(value.ArgumentList, JsonValueKind.Array);
        _ = HostConfigurationValueValidator.NormalizeJson(
            HostConfigurationValueValidator.SerializeEnvironment(value.Environment),
            JsonValueKind.Object);

        var health = value.HealthCheck;
        if (!Enum.IsDefined(health.Type) || health.Timeout <= TimeSpan.Zero ||
            health.Timeout.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            health.Timeout.TotalMilliseconds > int.MaxValue)
        {
            HostConfigurationValueValidator.Throw();
        }

        if (health.Type == ServiceHealthCheckType.Http)
        {
            if (!HostConfigurationValueValidator.IsSafeHttpPath(health.HttpPath))
            {
                HostConfigurationValueValidator.Throw();
            }
        }
        else if (health.HttpPath is not null)
        {
            HostConfigurationValueValidator.Throw();
        }
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}
