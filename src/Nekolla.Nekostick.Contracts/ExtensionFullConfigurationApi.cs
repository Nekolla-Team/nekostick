namespace Nekolla.Nekostick.Contracts;

/// <summary>Provides trusted extension access to complete persisted Host business and configuration data.</summary>
/// <remarks>
/// This capability is intentionally full-data and has no owner parameter or filtering boundary.
/// Values are exchanged only through immutable Contracts DTOs; service environment values and extension
/// settings may contain sensitive data. Replacements are complete: omitted routes, services, extension
/// records, and extension settings are deleted subject to Host validation and the active PortLease guard.
/// </remarks>
public interface IExtensionFullConfigurationApi
{
    /// <summary>Reads the complete immutable Host configuration snapshot.</summary>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The complete snapshot or a safe configuration error.</returns>
    ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the complete Host configuration atomically.</summary>
    /// <param name="expectedVersion">The global optimistic-concurrency version expected by the caller.</param>
    /// <param name="changes">The complete replacement configuration.</param>
    /// <param name="cancellationToken">The token used to cancel the replacement.</param>
    /// <returns>The committed version or a safe configuration error.</returns>
    ValueTask<ConfigurationWriteResult> ReplaceAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken = default);
}
