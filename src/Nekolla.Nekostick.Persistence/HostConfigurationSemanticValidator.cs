using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>
/// Provides the host publish-time semantic gate for contract-only configuration DTOs.
/// </summary>
/// <remarks>
/// This type is stateless and deliberately accepts no persistence objects. The host may
/// call it before publishing a snapshot or submitting a complete change set without
/// depending on EF entities, a DbContext, or database details.
/// </remarks>
public static class HostConfigurationSemanticValidator
{
    /// <summary>
    /// Validates a complete persisted snapshot for host publication.
    /// </summary>
    /// <param name="snapshot">The contract-only configuration snapshot.</param>
    /// <returns><see langword="true"/> when every semantic and persisted-version rule passes.</returns>
    public static bool TryValidateSnapshot(HostConfigurationSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        return TryValidate(() =>
        {
            HostConfigurationValidation.ValidateConfigurationValues(
                snapshot.GlobalSettings,
                snapshot.Routes,
                snapshot.Services,
                snapshot.ExtensionRecords,
                snapshot.ExtensionSettings);
            HostConfigurationGlobalValidator.ValidatePersistedVersions(snapshot);
        });
    }

    /// <summary>
    /// Validates a complete contract-only change set before host publication or persistence.
    /// </summary>
    /// <param name="changes">The complete replacement configuration change set.</param>
    /// <returns><see langword="true"/> when every semantic rule passes.</returns>
    public static bool TryValidateChangeSet(ConfigurationChangeSet? changes)
    {
        if (changes is null)
        {
            return false;
        }

        return TryValidate(() => HostConfigurationValidation.ValidateConfigurationValues(
            changes.GlobalSettings,
            changes.Routes,
            changes.Services,
            changes.ExtensionRecords,
            changes.ExtensionSettings));
    }

    /// <summary>
    /// Validates one contract-only extension settings DTO for host publication or persistence.
    /// </summary>
    /// <param name="settings">The extension settings DTO.</param>
    /// <returns><see langword="true"/> when the identifier, schema, version, and JSON pass.</returns>
    public static bool TryValidateExtensionSettings(ExtensionSettingsConfiguration? settings) =>
        settings is not null && TryValidate(() => HostConfigurationExtensionValidator.ValidateSettings(settings));

    internal static bool IsSafeExtensionId(string? value) => HostConfigurationValueValidator.IsSafeExtensionId(value);

    internal static bool IsUuidV7(Guid value) => HostConfigurationValueValidator.IsUuidV7(value);

    internal static string NormalizeJson(string? value, JsonValueKind? expectedKind) =>
        HostConfigurationValueValidator.NormalizeJson(value, expectedKind);

    internal static ImmutableArray<string> DeserializeStringArray(string value) =>
        HostConfigurationValueValidator.DeserializeStringArray(value);

    internal static ImmutableArray<HeaderRewriteConfiguration> DeserializeHeaderRewrites(string value) =>
        HostConfigurationValueValidator.DeserializeHeaderRewrites(value);

    internal static ImmutableDictionary<string, string> DeserializeEnvironment(string value) =>
        HostConfigurationValueValidator.DeserializeEnvironment(value);

    internal static string SerializeEnvironment(ImmutableDictionary<string, string> value) =>
        HostConfigurationValueValidator.SerializeEnvironment(value);

    internal static string SerializeJson<T>(T value) => HostConfigurationValueValidator.SerializeJson(value);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The public DTO validation boundary is deliberately fail-closed and never exposes validation exception details.")]
    private static bool TryValidate(Action validation)
    {
        try
        {
            validation();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal sealed class ConfigurationValidationException : Exception
    {
    }
}
