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

internal enum ExtensionCallbackKind
{
    /// <summary>Route handler, fallback, or route-hook dispatch; the invocation holds an active-request lease that generation replacement drains.</summary>
    Route,
    /// <summary>Event-queue subscriber; the consumer loop is awaited when the owning instance stops.</summary>
    Event,
    /// <summary>Extension task-scheduler callback; an independent logical context that inherits no callback constraints.</summary>
    Scheduler,
    /// <summary>Entrypoint lifecycle callback (start/stop/previous-stopped) awaited by the runtime under the publication gate.</summary>
    Lifecycle
}

internal static class ExtensionCallbackGuard
{
    private const int RouteBit = 1;
    private const int EventBit = 2;
    private const int SchedulerBit = 4;
    private const int LifecycleBit = 8;

    private static readonly AsyncLocal<int> Bits = new();

    internal static bool IsActive => Bits.Value != 0;

    /// <summary>Gets whether the current context is an entrypoint lifecycle callback awaited under the publication gate.</summary>
    internal static bool IsLifecycleActive => (Bits.Value & LifecycleBit) != 0;

    /// <summary>Gets whether the current context is torn down during generation replacement of the calling extension (route lease drain, event consumer, or tracked scheduler task).</summary>
    internal static bool IsSelfReplacementUnsafe => (Bits.Value & (RouteBit | EventBit | SchedulerBit)) != 0;

    internal static IDisposable Enter(ExtensionCallbackKind kind)
    {
        var prior = Bits.Value;
        // Scheduler callbacks are independent logical contexts: entrypoint lifecycle constraints
        // captured via ExecutionContext at task creation must not leak into them.
        Bits.Value = kind == ExtensionCallbackKind.Scheduler
            ? SchedulerBit
            : prior | ToBit(kind);
        return new Scope(prior);
    }

    private static int ToBit(ExtensionCallbackKind kind) => kind switch
    {
        ExtensionCallbackKind.Route => RouteBit,
        ExtensionCallbackKind.Event => EventBit,
        ExtensionCallbackKind.Scheduler => SchedulerBit,
        _ => LifecycleBit
    };

    private sealed class Scope : IDisposable
    {
        private readonly int _prior;

        public Scope(int prior) => _prior = prior;

        public void Dispose() => Bits.Value = _prior;
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
            new UnsupportedLogWriter(),
            new UnsupportedManagementApi(negotiatedVersion));

    internal static IExtensionSupervisorApi CreateSupervisor() => new UnsupportedSupervisorApi();
    internal static IExtensionManagementApi CreateManagement() =>
        CreateManagement(HostApiVersion.Current);

    internal static IExtensionManagementApi CreateManagement(HostApiVersion negotiatedVersion) =>
        new UnsupportedManagementApi(negotiatedVersion);

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

        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadForExtensionAsync(
            string extensionId,
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

    private sealed class UnsupportedManagementApi : IExtensionManagementApi
    {
        private readonly HostApiVersion _apiVersion;

        internal UnsupportedManagementApi(HostApiVersion apiVersion) => _apiVersion = apiVersion;

        public HostApiVersion ApiVersion => _apiVersion;

        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public ValueTask<ConfigurationWriteResult> EnableAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public ValueTask<ConfigurationWriteResult> DisableAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public ValueTask<ConfigurationWriteResult> ReloadAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public bool ReloadSoon(string extensionId) => false;

        public ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));

        public ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
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
