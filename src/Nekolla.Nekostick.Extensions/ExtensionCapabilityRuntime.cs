using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal static class ExtensionCallbackGuard
{
    private static readonly AsyncLocal<int> Depth = new();

    internal static bool IsActive => Depth.Value > 0;

    internal static IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            if (Depth.Value > 0)
            {
                Depth.Value--;
            }
        }
    }
}

internal sealed class ExtensionLifecycleApi : IExtensionLifecycleApi
{
    private readonly Func<ExtensionLifecycleStatus?> _status;
    private readonly Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>> _reload;
    private readonly Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>> _unload;

    internal ExtensionLifecycleApi(
        Func<ExtensionLifecycleStatus?> status,
        Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>> reload,
        Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>> unload)
    {
        _status = status;
        _reload = reload;
        _unload = unload;
    }

    public ExtensionLifecycleStatus? Status => _status();

    public ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(CancellationToken cancellationToken = default) =>
        ExtensionCallbackGuard.IsActive
            ? ValueTask.FromResult(new ExtensionLifecycleOperationResult(false, ExtensionLifecycleOperationCode.Reentrant, _status()))
            : _reload(cancellationToken);

    public ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(CancellationToken cancellationToken = default) =>
        ExtensionCallbackGuard.IsActive
            ? ValueTask.FromResult(new ExtensionLifecycleOperationResult(false, ExtensionLifecycleOperationCode.Reentrant, _status()))
            : _unload(cancellationToken);
}

internal static class UnsupportedExtensionCapabilities
{
    internal static ExtensionCapabilitySet Create() =>
        new(
            new UnsupportedConfigurationApi(),
            new UnsupportedRouteApi(),
            new UnsupportedServiceApi(),
            new UnsupportedEndpointApi(),
            new UnsupportedFullConfigurationApi());

    private sealed class UnsupportedConfigurationApi : IExtensionConfigurationApi
    {
        public HostApiVersion ApiVersion => HostApiVersion.Current;
        public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ExtensionConfigurationSnapshot>.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationWriteResult> ApplyAsync(long expectedVersion, ExtensionConfigurationChangeSet changes, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationWriteResult> WriteSettingsAsync(long expectedVersion, ExtensionSettingsConfiguration settings, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
    }

    private sealed class UnsupportedFullConfigurationApi : IExtensionFullConfigurationApi
    {
        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public ValueTask<ConfigurationWriteResult> ReplaceAsync(
            long expectedVersion,
            ConfigurationChangeSet changes,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));
    }

    private sealed class UnsupportedRouteApi : IExtensionRouteApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionRouteConfiguration route, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid routeId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
    }

    private sealed class UnsupportedServiceApi : IExtensionServiceApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionServiceConfiguration service, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        public ValueTask<ExtensionServiceOperationResult> StartAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId));
        public ValueTask<ExtensionServiceOperationResult> StopAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId));
        public ValueTask<ExtensionServiceOperationResult> RestartAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId));
    }

    private sealed class UnsupportedEndpointApi : IExtensionEndpointApi
    {
        public ImmutableArray<ExtensionEndpointLease> Current => ImmutableArray<ExtensionEndpointLease>.Empty;
        public ValueTask<ExtensionEndpointLease?> ResolveAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ExtensionEndpointLease?>(null);
    }
}
