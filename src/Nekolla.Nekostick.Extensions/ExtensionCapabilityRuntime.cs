using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal static class ExtensionApiCapabilityGate
{
    private static readonly HostApiVersion Api11Version = new(1, 1, 0);
    private static readonly HostApiVersion Api12Version = new(1, 2, 0);
    private static readonly HostApiVersion Api13Version = new(1, 3, 2);
    private static readonly HostApiVersion Api133Version = new(1, 3, 3);
    private static readonly HostApiVersion Api14Version = new(1, 4, 0);

    internal static bool IsApi11Supported(HostApiVersion host) =>
        host.Major == Api11Version.Major && host >= Api11Version;

    internal static bool IsApi12Supported(HostApiVersion host) =>
        host.Major == Api12Version.Major && host >= Api12Version;

    internal static bool IsApi13Supported(HostApiVersion host) =>
        host.Major == Api13Version.Major && host >= Api13Version;

    internal static bool IsApi133Supported(HostApiVersion host) =>
        host.Major == Api133Version.Major && host >= Api133Version;

    internal static bool IsApi14Supported(HostApiVersion host) =>
        host.Major == Api14Version.Major && host >= Api14Version;

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
    Lifecycle,
    /// <summary>Extension service-log sink callback; the invocation is isolated from lifecycle reentrancy.</summary>
    ServiceLog,
    /// <summary>Extension runtime-state sink callback; the invocation is isolated from lifecycle reentrancy.</summary>
    ServiceRuntimeState
}

internal static class ExtensionCallbackGuard
{
    private const int RouteBit = 1;
    private const int EventBit = 2;
    private const int SchedulerBit = 4;
    private const int LifecycleBit = 8;
    private const int ServiceLogBit = 16;
    private const int ServiceRuntimeStateBit = 32;
    private static readonly AsyncLocal<int> Bits = new();

    internal static bool IsActive => Bits.Value != 0;

    /// <summary>Gets whether the current context is an entrypoint lifecycle callback awaited under the publication gate.</summary>
    internal static bool IsLifecycleActive => (Bits.Value & LifecycleBit) != 0;

    /// <summary>Gets whether the current context is torn down during generation replacement of the calling extension (route lease drain, event consumer, tracked scheduler task, service-log callback, or runtime-state callback).</summary>
    internal static bool IsSelfReplacementUnsafe => (Bits.Value & (RouteBit | EventBit | SchedulerBit | ServiceLogBit | ServiceRuntimeStateBit)) != 0;

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
        ExtensionCallbackKind.ServiceLog => ServiceLogBit,
        ExtensionCallbackKind.ServiceRuntimeState => ServiceRuntimeStateBit,
        _ => LifecycleBit
    };

    private sealed class Scope : IDisposable
    {
        private readonly int _prior;

        public Scope(int prior) => _prior = prior;

        public void Dispose() => Bits.Value = _prior;
    }
}

internal interface IExtensionServiceOutputCleanup : IAsyncDisposable
{
    void DetachAll();
}

internal interface IExtensionServiceLogCleanup : IAsyncDisposable
{
    void DetachAll();
}

internal interface IExtensionServiceRuntimeStateCleanup : IAsyncDisposable
{
    void DetachAll();
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
            ? ValueTask.FromResult(
                new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Reentrant,
                    _status(),
                    new ExtensionErrorDetail("A reentrant extension reload request is not allowed from an extension callback.")))
            : _reload(cancellationToken);

    public ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(CancellationToken cancellationToken = default) =>
        ExtensionCallbackGuard.IsActive
            ? ValueTask.FromResult(
                new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Reentrant,
                    _status(),
                    new ExtensionErrorDetail("A reentrant extension unload request is not allowed from an extension callback.")))
            : _unload(cancellationToken);
}

/// <summary>Creates explicit unsupported facades for unavailable or unnegotiated capabilities.</summary>
/// <remarks>The API 1.3 and 1.4 members use these no-op/error facades on hosts below their negotiated versions or when a capability was not composed; they are never Host logging sinks.</remarks>
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
            new UnsupportedManagementApi(negotiatedVersion),
            null,
            CreateServiceOutput(),
            CreateServiceRuntimeState());

    internal static IExtensionSupervisorApi CreateSupervisor() => new UnsupportedSupervisorApi();
    internal static IExtensionManagementApi CreateManagement() =>
        CreateManagement(HostApiVersion.Current);

    internal static IExtensionManagementApi CreateManagement(HostApiVersion negotiatedVersion) =>
        new UnsupportedManagementApi(negotiatedVersion);

    internal static IExtensionRouteEvents CreateRouteEvents() => new UnsupportedRouteEvents();

    internal static IExtensionLogWriter CreateLogWriter() => new UnsupportedLogWriter();
    internal static IExtensionServiceOutputApi CreateServiceOutput() => new UnsupportedServiceOutputApi();
    internal static IExtensionServiceRuntimeStateApi CreateServiceRuntimeState() => new UnsupportedServiceRuntimeStateApi();
    internal static IExtensionLifecycleApi CreateLifecycle() => new UnsupportedLifecycleApi();
    internal static IExtensionDependencyApi CreateDependencyApi() => new UnsupportedDependencyApi();
    private static Guid SafeServiceId(Guid serviceId)
    {
        if (serviceId != Guid.Empty)
        {
            var text = serviceId.ToString("D");
            var variant = text[19];
            if (text[14] == '7' &&
                (variant == '8' || variant == '9' || variant == 'a' || variant == 'b'))
            {
                return serviceId;
            }
        }

        return Guid.CreateVersion7();
    }
    private static ConfigurationError UnsupportedConfigurationError(
        string operation,
        HostApiVersion? apiVersion = null) =>
        new(
            ConfigurationErrorCode.Unsupported,
            apiVersion is { } version
                ? $"The negotiated Host API version {version} does not support {operation}."
                : $"The extension host does not support {operation}.");

    private static ExtensionErrorDetail UnsupportedDetail(string capability) =>
        new($"The extension host does not support {capability}.");



    private sealed class UnsupportedConfigurationApi : IExtensionConfigurationApi
    {
        private readonly HostApiVersion _apiVersion;

        internal UnsupportedConfigurationApi(HostApiVersion apiVersion) => _apiVersion = apiVersion;

        public HostApiVersion ApiVersion => _apiVersion;
        public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ExtensionConfigurationSnapshot>.Failure(UnsupportedConfigurationError("extension configuration reads", _apiVersion)));
        public ValueTask<ConfigurationWriteResult> ApplyAsync(long expectedVersion, ExtensionConfigurationChangeSet changes, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(UnsupportedConfigurationError("extension configuration writes", _apiVersion)));
        public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(UnsupportedConfigurationError("extension settings reads", _apiVersion)));
        public ValueTask<ConfigurationWriteResult> WriteSettingsAsync(long expectedVersion, ExtensionSettingsConfiguration settings, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(UnsupportedConfigurationError("extension settings writes", _apiVersion)));
    }
    private sealed class UnsupportedFullConfigurationApi : IExtensionFullConfigurationApi
    {
        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    UnsupportedConfigurationError("full host configuration reads")));

        public ValueTask<ConfigurationWriteResult> ReplaceAsync(
            long expectedVersion,
            ConfigurationChangeSet changes,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("full host configuration writes")));
    }

    private sealed class UnsupportedRouteApi : IExtensionRouteApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>.Failure(UnsupportedConfigurationError("extension route reads")));
        public ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionRouteConfiguration route, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(UnsupportedConfigurationError("extension route writes")));
        public ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid routeId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(UnsupportedConfigurationError("extension route writes")));
    }
    private sealed class UnsupportedDependencyApi : IExtensionDependencyApi
    {
        public IExtensionDependencyContext GetDependencyContext(string extensionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
            return new ExtensionDependencyContext(
                extensionId,
                ExtensionDependencyState.Unavailable,
                optional: false,
                versionRange: string.Empty,
                installedVersion: null,
                contracts: null);
        }
    }


    private sealed class UnsupportedServiceApi : IExtensionServiceApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>.Failure(UnsupportedConfigurationError("extension service reads")));
        public ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionServiceConfiguration service, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(UnsupportedConfigurationError("extension service writes")));
        public ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationWriteResult.Failure(UnsupportedConfigurationError("extension service writes")));
        public ValueTask<ExtensionServiceOperationResult> StartAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId, UnsupportedDetail("service start operations")));
        public ValueTask<ExtensionServiceOperationResult> StopAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId, UnsupportedDetail("service stop operations")));
        public ValueTask<ExtensionServiceOperationResult> RestartAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId, UnsupportedDetail("service restart operations")));
    }
    private sealed class UnsupportedServiceOutputApi : IExtensionServiceOutputApi
    {
        public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ExtensionServiceOutputStreamResult(
                    false,
                    ExtensionServiceOutputCode.Unsupported,
                    SafeServiceId(serviceId),
                    null,
                    UnsupportedDetail("service output streaming")));
        public ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
            Guid serviceId,
            IExtensionServiceLogSink sink,
            long? sinceSequence = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ExtensionServiceLogSubscriptionResult(
                    false,
                    ExtensionServiceLogCode.Unsupported,
                    SafeServiceId(serviceId),
                    null,
                    UnsupportedDetail("service log subscriptions")));
    }
    private sealed class UnsupportedServiceRuntimeStateApi : IExtensionServiceRuntimeStateApi
    {
        public ValueTask<ExtensionServiceRuntimeStateSubscriptionResult> SubscribeStatesAsync(
            IExtensionServiceRuntimeStateSink sink,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ExtensionServiceRuntimeStateSubscriptionResult(
                    false,
                    ExtensionServiceRuntimeStateSubscriptionCode.Unsupported,
                    null,
                    UnsupportedDetail("service runtime-state subscriptions")));
    }


    private sealed class UnsupportedEndpointApi : IExtensionEndpointApi
    {
        public ImmutableArray<ExtensionEndpointLease> Current => ImmutableArray<ExtensionEndpointLease>.Empty;
        public ValueTask<ExtensionEndpointResolutionResult> ResolveAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ExtensionEndpointResolutionResult.Failure(
                    ExtensionEndpointResolutionFailureCode.Unsupported,
                    UnsupportedDetail("endpoint resolution")));
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
                    null,
                    UnsupportedDetail("extension reload requests")));

        public ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Unsupported,
                    null,
                    UnsupportedDetail("extension unload requests")));
    }

    private sealed class UnsupportedSupervisorApi : IExtensionSupervisorApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Failure(
                    UnsupportedConfigurationError("supervisor state reads")));

        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadForExtensionAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Failure(
                    UnsupportedConfigurationError("supervisor state reads")));

        public ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Failure(
                    UnsupportedConfigurationError("supervisor state reads")));

        public ValueTask<ConfigurationWriteResult> ResumeAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("supervisor resume operations")));

        public ValueTask<ConfigurationWriteResult> RestartAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("supervisor restart operations")));
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
                    UnsupportedConfigurationError("extension management list operations", _apiVersion)));

        public ValueTask<ConfigurationWriteResult> EnableAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("extension management enable operations", _apiVersion)));

        public ValueTask<ConfigurationWriteResult> DisableAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("extension management disable operations", _apiVersion)));

        public ValueTask<ConfigurationWriteResult> ReloadAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("extension management reload operations", _apiVersion)));

        public ExtensionReloadScheduleResult ReloadSoon(string extensionId) =>
            ExtensionReloadScheduleResult.Failure(
                ExtensionReloadScheduleFailureCode.Unsupported,
                new ExtensionErrorDetail(
                    $"The negotiated Host API version {_apiVersion} does not support extension reload scheduling."));

        public ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    UnsupportedConfigurationError("extension management record deletion", _apiVersion)));

        public ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                    UnsupportedConfigurationError("extension management refresh operations", _apiVersion)));
    }

    private sealed class UnsupportedRouteEvents : IExtensionRouteEvents
    {
        public ExtensionRouteRegistrationResult TrySubscribe(
            Func<ExtensionEvent, CancellationToken, ValueTask> callback) =>
            ExtensionRouteRegistrationResult.Unsupported;

        public ExtensionRouteRegistrationResult TryRegisterHook(
            ExtensionRouteEventStage stage,
            Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback) =>
            ExtensionRouteRegistrationResult.Unsupported;
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
