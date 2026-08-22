using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal static class ExtensionApiCapabilityGate
{
    private static readonly HostApiVersion Api11Version = new(1, 1, 0);
    private static readonly HostApiVersion Api12Version = new(1, 2, 0);

    internal static bool IsApi11Supported(HostApiVersion host) =>
        host.Major == Api11Version.Major && host >= Api11Version;

    internal static bool IsApi12Supported(HostApiVersion host) =>
        host.Major == Api12Version.Major && host >= Api12Version;
}

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

/// <summary>Creates explicit unsupported facades for unavailable or unnegotiated capabilities.</summary>
/// <remarks>The API 1.3 members use these no-op/error facades on hosts below 1.3 or when a capability was not composed; they are never Host logging sinks.</remarks>
internal static class UnsupportedExtensionCapabilities
{
    internal static ExtensionCapabilitySet Create() => Create(HostApiVersion.Current);

    internal static ExtensionCapabilitySet Create(HostApiVersion negotiatedVersion) =>
        new(
            new UnsupportedConfigurationApi(negotiatedVersion),
            new UnsupportedRouteApi(),
            new UnsupportedServiceApi(),
            new UnsupportedEndpointApi(),
            new UnsupportedFullConfigurationApi(),
            new UnsupportedSupervisorApi(),
            new UnsupportedRouteEvents(),
            new UnsupportedLogWriter());

    internal static IExtensionSupervisorApi CreateSupervisor() => new UnsupportedSupervisorApi();

    internal static IExtensionRouteEvents CreateRouteEvents() => new UnsupportedRouteEvents();

    internal static IExtensionLogWriter CreateLogWriter() => new UnsupportedLogWriter();
    internal static IExtensionLifecycleApi CreateLifecycle() => new UnsupportedLifecycleApi();

    private sealed class UnsupportedConfigurationApi : IExtensionConfigurationApi
    {
        private readonly HostApiVersion _apiVersion;

        internal UnsupportedConfigurationApi(HostApiVersion apiVersion) => _apiVersion = apiVersion;

        public HostApiVersion ApiVersion => _apiVersion;
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
    private sealed class UnsupportedLifecycleApi : IExtensionLifecycleApi
    {
        public ExtensionLifecycleStatus? Status => null;

        public ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Unsupported,
                    null));

        public ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Unsupported,
                    null));
    }

    private sealed class UnsupportedSupervisorApi : IExtensionSupervisorApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));
    }

    private sealed class UnsupportedRouteEvents : IExtensionRouteEvents
    {
        public bool TrySubscribe(
            Func<ExtensionEvent, CancellationToken, ValueTask> callback) => false;

        public bool TryRegisterHook(
            ExtensionRouteEventStage stage,
            Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback) => false;
    }

    /// <summary>Represents the unsupported API 1.3 custom logging compatibility path.</summary>
    /// <remarks>Writes are intentionally discarded because no Host-attributed sink was negotiated.</remarks>
    private sealed class UnsupportedLogWriter : IExtensionLogWriter
    {
        public void WriteText(ExtensionLogLevel level, string text)
        {
        }
    }

}
