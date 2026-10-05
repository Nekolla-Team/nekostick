using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// <param name="logger">The optional persistence logger.</param>
    public static bool TryValidateSnapshot(HostConfigurationSnapshot? snapshot, ILogger? logger = null) =>
        TryValidateSnapshot(snapshot, out _, logger);

    internal static bool TryValidateSnapshot(
        HostConfigurationSnapshot? snapshot,
        out string? validationMessage,
        ILogger? logger = null)
    {
        validationMessage = null;
        if (snapshot is null)
        {
            PersistenceLogMessages.ValidationRejected(logger ?? NullLogger.Instance, "ValidateSnapshot", "configuration");
            return false;
        }

        return TryValidate(() =>
        {
            HostConfigurationValidation.ValidateConfigurationValues(
                snapshot.GlobalSettings,
                snapshot.Routes,
                snapshot.Services,
                snapshot.ExtensionRecords,
                snapshot.ExtensionSettings,
                logger);
            HostConfigurationGlobalValidator.ValidatePersistedVersions(snapshot);
        }, logger, "ValidateSnapshot", out validationMessage);
    }

    /// <summary>
    /// Validates a complete contract-only change set before host publication or persistence.
    /// </summary>
    /// <param name="changes">The complete replacement configuration change set.</param>
    /// <returns><see langword="true"/> when every semantic rule passes.</returns>
    /// <param name="logger">The optional persistence logger.</param>
    public static bool TryValidateChangeSet(ConfigurationChangeSet? changes, ILogger? logger = null) =>
        TryValidateChangeSet(changes, out _, logger);

    internal static bool TryValidateChangeSet(
        ConfigurationChangeSet? changes,
        out string? validationMessage,
        ILogger? logger = null)
    {
        validationMessage = null;
        if (changes is null)
        {
            PersistenceLogMessages.ValidationRejected(logger ?? NullLogger.Instance, "ValidateChangeSet", "configuration");
            return false;
        }

        return TryValidate(() => HostConfigurationValidation.ValidateConfigurationValues(
            changes.GlobalSettings,
            changes.Routes,
            changes.Services,
            changes.ExtensionRecords,
            changes.ExtensionSettings,
            logger), logger, "ValidateChangeSet", out validationMessage);
    }

    internal static bool TryValidateChangeSetForWrite(
        ConfigurationChangeSet? changes,
        out string? validationMessage,
        out List<string>? environmentValidationMessages,
        ILogger? logger = null)
    {
        validationMessage = null;
        environmentValidationMessages = null;
        if (changes is null)
        {
            PersistenceLogMessages.ValidationRejected(logger ?? NullLogger.Instance, "ValidateChangeSet", "configuration");
            return false;
        }

        List<string>? collectedEnvironmentValidationMessages = null;
        if (!TryValidate(
                () => HostConfigurationValidation.ValidateConfigurationValuesForChangeSet(
                    changes.GlobalSettings,
                    changes.Routes,
                    changes.Services,
                    changes.ExtensionRecords,
                    changes.ExtensionSettings,
                    logger,
                    ref collectedEnvironmentValidationMessages),
                logger,
                "ValidateChangeSet",
                out validationMessage))
        {
            return false;
        }

        if (collectedEnvironmentValidationMessages is null)
        {
            return true;
        }

        var effectiveLogger = logger ?? NullLogger.Instance;
        foreach (var message in collectedEnvironmentValidationMessages)
        {
            PersistenceLogMessages.SemanticValidationFailed(
                effectiveLogger,
                new ConfigurationValidationException(message),
                "ValidateChangeSet",
                "configuration");
        }

        environmentValidationMessages = collectedEnvironmentValidationMessages;
        return false;
    }

    /// <summary>
    /// Validates one contract-only extension settings DTO for host publication or persistence.
    /// </summary>
    /// <param name="settings">The extension settings DTO.</param>
    /// <returns><see langword="true"/> when the identifier, schema, version, and JSON pass.</returns>
    /// <param name="logger">The optional persistence logger.</param>
    public static bool TryValidateExtensionSettings(ExtensionSettingsConfiguration? settings, ILogger? logger = null) =>
        settings is not null
            ? TryValidate(
                () => HostConfigurationExtensionValidator.ValidateSettings(settings),
                logger,
                "ValidateExtensionSettings")
            : RejectNull(logger, "ValidateExtensionSettings");

    /// <summary>Validates a collection of extension records for persistence.</summary>
    /// <param name="records">The extension records to validate.</param>
    /// <returns><see langword="true"/> when every record is semantically valid and unique.</returns>
    /// <param name="logger">The optional persistence logger.</param>
    internal static bool TryValidateExtensionRecords(
        IEnumerable<ExtensionRecordConfiguration>? records,
        ILogger? logger = null)
    {
        if (records is null)
        {
            return RejectNull(logger, "ValidateExtensionRecords");
        }

        return TryValidate(() =>
        {
            var values = records.ToArray();
            HostConfigurationValueValidator.ValidateUniqueText(
                values.Select(value => value?.ExtensionId),
                HostConfigurationValueValidator.MaxExtensionIdLength);
            foreach (var record in values)
            {
                HostConfigurationExtensionValidator.ValidateRecord(record);
            }
        }, logger, "ValidateExtensionRecords");
    }

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

    private static bool TryValidate(Action validation, ILogger? logger, string operation) =>
        TryValidate(validation, logger, operation, out _);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The DTO validation boundary is fail-closed and forwards only curated validation messages, never raw exception details.")]
    private static bool TryValidate(
        Action validation,
        ILogger? logger,
        string operation,
        out string? validationMessage)
    {
        validationMessage = null;
        try
        {
            validation();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            JsonException or
            ConfigurationValidationException)
        {
            if (exception is ConfigurationValidationException validationException)
            {
                validationMessage = validationException.SafeMessage;
            }

            PersistenceLogMessages.SemanticValidationFailed(
                logger ?? NullLogger.Instance,
                exception,
                operation,
                "configuration");
            return false;
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.OperationFailed(
                logger ?? NullLogger.Instance,
                exception,
                operation,
                "configuration");
            return false;
        }
    }

    private static bool RejectNull(ILogger? logger, string operation)
    {
        PersistenceLogMessages.ValidationRejected(logger ?? NullLogger.Instance, operation, "configuration");
        return false;
    }

    internal sealed class ConfigurationValidationException : Exception
    {
        internal ConfigurationValidationException(string? safeMessage = null)
            : base(safeMessage)
        {
            SafeMessage = safeMessage;
        }

        internal string? SafeMessage { get; }
    }
}
