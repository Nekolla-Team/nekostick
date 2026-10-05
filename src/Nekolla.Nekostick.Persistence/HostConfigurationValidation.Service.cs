using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationServiceValidator
{
    internal static void Validate(ServiceConfiguration? value)
    {
        List<string>? environmentValidationMessages = null;
        ValidateCore(value, ref environmentValidationMessages, collectEnvironmentViolations: false);
    }

    internal static void ValidateForChangeSet(
        ServiceConfiguration? value,
        ref List<string>? environmentValidationMessages) =>
        ValidateCore(value, ref environmentValidationMessages, collectEnvironmentViolations: true);

    private static void ValidateCore(
        ServiceConfiguration? value,
        ref List<string>? environmentValidationMessages,
        bool collectEnvironmentViolations)
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

        var environmentViolationCount = environmentValidationMessages?.Count ?? 0;

        foreach (var pair in value.Environment)
        {
            if (!collectEnvironmentViolations)
            {
                if (!HostConfigurationValueValidator.IsSafeEnvironmentKey(pair.Key) ||
                    !HostConfigurationValueValidator.IsSafeEnvironmentValue(pair.Value))
                {
                    HostConfigurationValueValidator.Throw();
                }

                continue;
            }

            var keyFailure = HostConfigurationValueValidator.GetEnvironmentKeyFailureReason(pair.Key);
            var valueFailure = HostConfigurationValueValidator.GetEnvironmentValueFailureReason(pair.Value);
            var renderableKey = keyFailure != HostConfigurationValueValidator.EnvironmentValidationFailureReason.None ||
                valueFailure != HostConfigurationValueValidator.EnvironmentValidationFailureReason.None
                    ? GetRenderableEnvironmentKey(pair.Key)
                    : null;
            if (keyFailure != HostConfigurationValueValidator.EnvironmentValidationFailureReason.None)
            {
                environmentValidationMessages ??= new List<string>();
                environmentValidationMessages.Add(CreateEnvironmentKeyViolationMessage(
                    value.Id,
                    renderableKey,
                    keyFailure));
            }

            if (valueFailure != HostConfigurationValueValidator.EnvironmentValidationFailureReason.None)
            {
                environmentValidationMessages ??= new List<string>();
                environmentValidationMessages.Add(CreateEnvironmentValueViolationMessage(
                    value.Id,
                    renderableKey,
                    valueFailure));
            }
        }

        var hasEnvironmentViolations = environmentValidationMessages is not null &&
            environmentValidationMessages.Count > environmentViolationCount;
        HostConfigurationValueValidator.EnsureSerializedJson(value.ArgumentList, JsonValueKind.Array);
        if (!hasEnvironmentViolations)
        {
            _ = HostConfigurationValueValidator.NormalizeJson(
                HostConfigurationValueValidator.SerializeEnvironment(value.Environment),
                JsonValueKind.Object);
        }

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

    private static string CreateEnvironmentKeyViolationMessage(
        Guid serviceId,
        string? renderableKey,
        HostConfigurationValueValidator.EnvironmentValidationFailureReason failureReason)
    {
        var field = renderableKey is null
            ? "Environment key"
            : $"Environment key '{renderableKey}'";
        var cause = failureReason switch
        {
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.NullOrWhitespace => "it is null or whitespace",
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.TooLong =>
                $"it exceeds the {HostConfigurationValueValidator.MaxEnvironmentKeyLength}-character limit",
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.ControlCharacter =>
                "it contains a control character",
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.EqualsSign => "it contains '='",
            _ => "it is invalid"
        };

        return $"Service '{serviceId:D}' {field} is invalid: {cause}.";
    }

    private static string CreateEnvironmentValueViolationMessage(
        Guid serviceId,
        string? renderableKey,
        HostConfigurationValueValidator.EnvironmentValidationFailureReason failureReason)
    {
        var cause = failureReason switch
        {
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.NullValue => "it is null",
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.TooLong =>
                $"it exceeds the {HostConfigurationValueValidator.MaxEnvironmentValueLength}-character limit",
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.ControlCharacter =>
                "it contains a control character",
            HostConfigurationValueValidator.EnvironmentValidationFailureReason.MalformedTemplatePlaceholder =>
                "it contains a malformed template placeholder",
            _ => "it is invalid"
        };

        return renderableKey is null
            ? $"Service '{serviceId:D}' Environment value is invalid: {cause}."
            : $"Service '{serviceId:D}' Environment value for key '{renderableKey}' is invalid: {cause}.";
    }

    private static string? GetRenderableEnvironmentKey(string? key)
    {
        if (key is null || key.Length > HostConfigurationValueValidator.MaxEnvironmentKeyLength ||
            ContainsControlCharacter(key))
        {
            return null;
        }

        return EscapeEnvironmentKey(key);
    }

    private static string EscapeEnvironmentKey(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}
