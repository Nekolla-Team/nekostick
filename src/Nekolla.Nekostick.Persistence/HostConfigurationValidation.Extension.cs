using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationExtensionValidator
{
    private static readonly Regex SemanticVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(HostConfigurationValueValidator.RegexTimeoutMilliseconds));

    internal static bool IsValidVersion(string? value) =>
        value is not null &&
        value.Length <= HostConfigurationValueValidator.MaxExtensionIdLength &&
        SemanticVersionPattern.IsMatch(value);

    internal static void ValidateRecord(ExtensionRecordConfiguration? value)
    {
        if (value is null || !HostConfigurationValueValidator.IsSafeExtensionId(value.ExtensionId) ||
            !IsValidVersion(value.Version) || !Enum.IsDefined(value.LoadState) ||
            value.RecordVersion < 0)
        {
            HostConfigurationValueValidator.Throw();
        }
    }

    internal static void ValidateSettings(ExtensionSettingsConfiguration? value)
    {
        if (value is null || !HostConfigurationValueValidator.IsSafeExtensionId(value.ExtensionId) ||
            value.SchemaVersion < 0 || value.Version < 0)
        {
            HostConfigurationValueValidator.Throw();
        }

        _ = HostConfigurationValueValidator.NormalizeJson(value.SettingsJson, null);
    }
}
