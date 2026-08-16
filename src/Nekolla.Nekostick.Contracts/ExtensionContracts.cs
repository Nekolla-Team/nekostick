namespace Nekolla.Nekostick.Contracts;

/// <summary>Describes the externally visible extension load state.</summary>
public enum ExtensionLoadState
{
    /// <summary>The extension has been discovered but not loaded.</summary>
    Discovered,

    /// <summary>The extension is loaded and serving.</summary>
    Loaded,

    /// <summary>The extension is stopping or stopped.</summary>
    Stopped,

    /// <summary>The extension failed validation or execution.</summary>
    Failed,

    /// <summary>The extension is being unloaded.</summary>
    Unloading
}

/// <summary>Describes one stable extension installation record.</summary>
public sealed record ExtensionRecordConfiguration
{
    /// <summary>Creates an extension record DTO.</summary>
    /// <param name="extensionId">The stable manifest identifier.</param>
    /// <param name="version">The installed extension version.</param>
    /// <param name="loadState">The persisted public load state.</param>
    /// <param name="createdAt">The UTC creation timestamp.</param>
    /// <param name="updatedAt">The UTC update timestamp.</param>
    /// <param name="recordVersion">The optimistic-concurrency version.</param>
    public ExtensionRecordConfiguration(
        string extensionId,
        string version,
        ExtensionLoadState loadState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long recordVersion)
    {
        ExtensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        Version = string.IsNullOrWhiteSpace(version)
            ? throw new ArgumentException("An extension version is required.", nameof(version))
            : version;
        LoadState = loadState;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        RecordVersion = recordVersion < 0
            ? throw new ArgumentOutOfRangeException(nameof(recordVersion))
            : recordVersion;
    }

    /// <summary>Gets the stable manifest identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the installed semantic version text.</summary>
    public string Version { get; }

    /// <summary>Gets the public load state.</summary>
    public ExtensionLoadState LoadState { get; }

    /// <summary>Gets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets the record optimistic-concurrency version.</summary>
    public long RecordVersion { get; }
}

/// <summary>Contains one extension-owned immutable JSON settings document.</summary>
public sealed record ExtensionSettingsConfiguration
{
    /// <summary>Creates extension settings.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="schemaVersion">The extension settings schema version.</param>
    /// <param name="settingsJson">The validated JSON document.</param>
    /// <param name="version">The optimistic-concurrency version.</param>
    public ExtensionSettingsConfiguration(string extensionId, int schemaVersion, string settingsJson, long version)
    {
        ExtensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        SchemaVersion = schemaVersion < 0
            ? throw new ArgumentOutOfRangeException(nameof(schemaVersion))
            : schemaVersion;
        SettingsJson = settingsJson ?? throw new ArgumentNullException(nameof(settingsJson));
        Version = version < 0 ? throw new ArgumentOutOfRangeException(nameof(version)) : version;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the extension settings schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the validated extension-owned JSON. Consumers must treat it as sensitive.</summary>
    public string SettingsJson { get; }

    /// <summary>Gets the optimistic-concurrency version.</summary>
    public long Version { get; }
}
