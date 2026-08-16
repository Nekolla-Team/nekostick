using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

/// <summary>Applies the process read-only policy at the host configuration API boundary.</summary>
internal sealed class HostConfigApiReadOnlyDecorator : IHostConfigApi
{
    private readonly IHostConfigApi _inner;
    private readonly bool _readOnly;

    /// <summary>Creates a host configuration API policy decorator.</summary>
    public HostConfigApiReadOnlyDecorator(IHostConfigApi inner, HostRuntimeOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(options);
        _readOnly = options.ReadOnly;
    }

    /// <inheritdoc />
    public HostApiVersion ApiVersion => _inner.ApiVersion;

    /// <inheritdoc />
    public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        _inner.ReadSnapshotAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConfigurationWriteResult> WriteSnapshotAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken = default) =>
        _readOnly
            ? ValueTask.FromResult(ReadOnlyWriteFailure())
            : _inner.WriteSnapshotAsync(expectedVersion, changes, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadExtensionSettingsAsync(
        string extensionId,
        CancellationToken cancellationToken = default) =>
        _inner.ReadExtensionSettingsAsync(extensionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConfigurationWriteResult> WriteExtensionSettingsAsync(
        string extensionId,
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default) =>
        _readOnly
            ? ValueTask.FromResult(ReadOnlyWriteFailure())
            : _inner.WriteExtensionSettingsAsync(extensionId, expectedVersion, settings, cancellationToken);

    private static ConfigurationWriteResult ReadOnlyWriteFailure() =>
        ConfigurationWriteResult.Failure(
            new ConfigurationError(ConfigurationErrorCode.Unsupported));
}
