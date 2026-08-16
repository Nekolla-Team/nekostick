namespace Nekolla.Nekostick.Contracts;

/// <summary>Provides the minimal safe host configuration contract for trusted extensions.</summary>
public interface IHostConfigApi
{
    /// <summary>Gets the semantic version of this host contract implementation.</summary>
    HostApiVersion ApiVersion { get; }

    /// <summary>Reads the last complete validated configuration snapshot.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A snapshot or safe errors.</returns>
    ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically commits a complete configuration change set.</summary>
    /// <param name="expectedVersion">The caller's expected global version.</param>
    /// <param name="changes">The immutable replacement values.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> WriteSnapshotAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one extension's persisted settings document.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The settings document or safe errors.</returns>
    ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadExtensionSettingsAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically writes one extension's settings document.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="expectedVersion">The caller's expected settings version.</param>
    /// <param name="settings">The immutable settings DTO.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> WriteExtensionSettingsAsync(
        string extensionId,
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default);
}
