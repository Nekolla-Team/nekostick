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
    Unloading,

    /// <summary>The extension is persistently disabled.</summary>
    Disabled
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
    /// <param name="contentHash">The optional canonical content digest in <c>sha256:&lt;hex&gt;</c> form; producers MUST emit lowercase hexadecimal digits.</param>
    public ExtensionRecordConfiguration(
        string extensionId,
        string version,
        ExtensionLoadState loadState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long recordVersion,
        string? contentHash = null)
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
        ContentHash = ValidateContentHash(contentHash);
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

    /// <summary>Gets the optional SHA-256 content digest; producers MUST emit lowercase hexadecimal digits, or <see langword="null" /> when not recorded.</summary>
    public string? ContentHash { get; }

    internal static string? ValidateContentHash(string? contentHash)
    {
        if (contentHash is null)
        {
            return null;
        }

        if (contentHash.Length != 71 || !contentHash.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The content hash must use sha256:<hex> form with 64 hexadecimal digits.", nameof(contentHash));
        }

        for (var index = 7; index < contentHash.Length; index++)
        {
            var character = contentHash[index];
            if ((character is < '0' or > '9') &&
                (character is < 'a' or > 'f') &&
                (character is < 'A' or > 'F'))
            {
                throw new ArgumentException("The content hash must use sha256:<hex> form with 64 hexadecimal digits.", nameof(contentHash));
            }
        }

        return contentHash;
    }
}

/// <summary>Identifies the most recent complete configuration snapshot outcome.</summary>
public enum ExtensionHostSnapshotState
{
    /// <summary>No snapshot outcome has been observed.</summary>
    Unknown,

    /// <summary>The most recent complete snapshot was accepted and published.</summary>
    Accepted,

    /// <summary>The most recent candidate snapshot was rejected.</summary>
    Rejected
}

/// <summary>Identifies the safe host readiness state exposed to an extension.</summary>
public enum ExtensionHostReadinessState
{
    /// <summary>No readiness observation is available.</summary>
    Unknown,

    /// <summary>No validated snapshot is available.</summary>
    Unready,

    /// <summary>A validated snapshot and database are available.</summary>
    Ready,

    /// <summary>A snapshot remains available while persistence capabilities are degraded.</summary>
    Degraded,

    /// <summary>
    /// A configuration publication is in flight: no validated snapshot is available yet, but readiness
    /// advances when the publication completes. Distinct from <see cref="Unready" />, which means no
    /// publication is progressing.
    /// </summary>
    Publishing
}

/// <summary>Contains immutable, non-sensitive host state visible to an extension.</summary>
/// <remarks>
/// This DTO intentionally excludes connection strings, secrets, environment variable values, process handles,
/// and other host implementation details. The host may return <see cref="Unavailable" /> before API 1.3.3 state
/// is negotiated.
/// </remarks>
public sealed record ExtensionHostInfoSnapshot
{
    /// <summary>Creates a safe host information snapshot.</summary>
    /// <param name="nodeId">The stable node identifier, or <see langword="null" /> when unavailable.</param>
    /// <param name="readOnly">Whether configuration writes are disabled for this process.</param>
    /// <param name="extensionsSkipped">Whether extension loading is disabled for this process.</param>
    /// <param name="supervisorDisabled">Whether process supervision is disabled for this process.</param>
    /// <param name="databaseAvailable">Whether the database was reachable during the latest runtime operation.</param>
    /// <param name="snapshotAvailable">Whether a complete configuration snapshot is currently published.</param>
    /// <param name="configurationValid">Whether the current published snapshot is valid.</param>
    /// <param name="publishedConfigurationVersion">The current published configuration version, when available.</param>
    /// <param name="lastSnapshotState">The most recent snapshot acceptance or rejection state.</param>
    /// <param name="lastSnapshotStateAt">The UTC time of the most recent snapshot state transition, when known.</param>
    /// <param name="readiness">The safe host readiness state.</param>
    public ExtensionHostInfoSnapshot(
        string? nodeId,
        bool readOnly,
        bool extensionsSkipped,
        bool supervisorDisabled,
        bool databaseAvailable,
        bool snapshotAvailable,
        bool configurationValid,
        long? publishedConfigurationVersion,
        ExtensionHostSnapshotState lastSnapshotState,
        DateTimeOffset? lastSnapshotStateAt,
        ExtensionHostReadinessState readiness)
    {
        if (nodeId is { Length: > 128 })
        {
            throw new ArgumentException("The node identifier is too long.", nameof(nodeId));
        }

        if (publishedConfigurationVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(publishedConfigurationVersion));
        }

        NodeId = nodeId;
        ReadOnly = readOnly;
        ExtensionsSkipped = extensionsSkipped;
        SupervisorDisabled = supervisorDisabled;
        DatabaseAvailable = databaseAvailable;
        SnapshotAvailable = snapshotAvailable;
        ConfigurationValid = configurationValid;
        PublishedConfigurationVersion = publishedConfigurationVersion;
        LastSnapshotState = lastSnapshotState;
        LastSnapshotStateAt = lastSnapshotStateAt?.ToUniversalTime();
        Readiness = readiness;
    }

    /// <summary>Gets a safe unavailable host information snapshot.</summary>
    public static ExtensionHostInfoSnapshot Unavailable { get; } = new(
        nodeId: null,
        readOnly: false,
        extensionsSkipped: false,
        supervisorDisabled: false,
        databaseAvailable: false,
        snapshotAvailable: false,
        configurationValid: false,
        publishedConfigurationVersion: null,
        lastSnapshotState: ExtensionHostSnapshotState.Unknown,
        lastSnapshotStateAt: null,
        readiness: ExtensionHostReadinessState.Unknown);

    /// <summary>Gets the stable node identifier, when available.</summary>
    public string? NodeId { get; }

    /// <summary>Gets whether configuration writes are disabled for this process.</summary>
    public bool ReadOnly { get; }

    /// <summary>Gets whether extension loading is disabled for this process.</summary>
    public bool ExtensionsSkipped { get; }

    /// <summary>Gets whether process supervision is disabled for this process.</summary>
    public bool SupervisorDisabled { get; }

    /// <summary>Gets whether the database was reachable during the latest runtime operation.</summary>
    public bool DatabaseAvailable { get; }

    /// <summary>Gets whether a complete configuration snapshot is currently published.</summary>
    public bool SnapshotAvailable { get; }

    /// <summary>Gets whether the current published snapshot is valid.</summary>
    public bool ConfigurationValid { get; }

    /// <summary>Gets the current published configuration version, when available.</summary>
    public long? PublishedConfigurationVersion { get; }

    /// <summary>Gets the most recent snapshot acceptance or rejection state.</summary>
    public ExtensionHostSnapshotState LastSnapshotState { get; }

    /// <summary>Gets the UTC time of the most recent snapshot state transition, when known.</summary>
    public DateTimeOffset? LastSnapshotStateAt { get; }

    /// <summary>Gets the safe host readiness state.</summary>
    /// <remarks>
    /// Lifecycle callbacks (for example <c>StartAsync</c>) run inside a publication: readiness is then
    /// <see cref="ExtensionHostReadinessState.Publishing" /> (or <see cref="ExtensionHostReadinessState.Unready" />
    /// on older hosts) by construction, never <see cref="ExtensionHostReadinessState.Ready" />. Treat this
    /// value as observational during lifecycle callbacks; do not gate the extension's main work on it there.
    /// </remarks>
    public ExtensionHostReadinessState Readiness { get; }
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
