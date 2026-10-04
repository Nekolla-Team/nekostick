using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Extensions;

internal sealed partial class ExtensionInstance : IAsyncDisposable
{
    private static readonly TimeSpan CapabilityCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly ExtensionLoadHandle _loadHandle;
    private readonly ExtensionHostBridge _bridge;
    private readonly IAsyncDisposable? _capabilityCleanup;
    private readonly IExtensionServiceLogCleanup? _serviceLogCapabilityCleanup;
    private readonly IExtensionServiceRuntimeStateCleanup? _runtimeStateCapabilityCleanup;
    private IExtensionEntrypoint? _entrypoint;
    private Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>>? _reloadCallback;
    private Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>>? _unloadCallback;
    private Func<Exception, ValueTask>? _failureCallback;
    private Task<bool>? _stopTask;
    private int _activeRequests;
    private readonly ExtensionTaskTracker _tasks;
    private readonly ExtensionEventQueue _events;
    private readonly ExtensionContractRegistry _contracts;
    private readonly HashSet<string> _contractConsumers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ContractId, string ConsumerId), object> _contractProxies = new();
    private readonly ExtensionFailureTracker _failures = new();
    private readonly ExtensionHandlerRegistry _registry = new();
    private readonly ExtensionRouteRegistrationSet _routeRegistrations;
    private ExtensionLoadState _state = ExtensionLoadState.Discovered;
    private ExtensionFailureCode _lastFailure;
    private ExtensionErrorDetail? _lastFailureDetail;
    private ExtensionStatus? _reportedStatus;
    private readonly ILogger? _logger;
    internal ExtensionInstance(
        ExtensionManifest manifest,
        ExtensionLoadHandle loadHandle,
        HostApiVersion hostApiVersion,
        ExtensionSettingsConfiguration? settings,
        Func<string, Type, SemVersionRange, ExtensionContractProviderResolution> resolveProvider,
        IReadOnlyDictionary<string, SemVersion> availableDependencyVersions,
        IExtensionCapabilityFactory? capabilityFactory,
        ImmutableArray<Guid> routeIds = default,
        string? dataDirectory = null,
        ILogger? logger = null)
    {
        Manifest = manifest;
        Settings = settings;
        _loadHandle = loadHandle;
        _logger = logger;
        _events = new ExtensionEventQueue(NotifyFailureAsync, onDrop: RecordDroppedEvent, logger: logger);
        _routeRegistrations = new ExtensionRouteRegistrationSet(
            manifest.Id,
            routeIds,
            // Event callbacks run on the queue consumer thread; track each as an active request
            // so the drain waits for in-flight event handlers before the stop pipeline proceeds.
            callback => _events.TrySubscribe(async (@event, cancellationToken) =>
            {
                if (!TryEnterRequest())
                {
                    // Draining or stopped: the callback cannot run; count the skip without
                    // stamping a queue-full failure on a cleanly reloading extension.
                    _events.RecordSkipped();
                    return;
                }

                try
                {
                    await callback(@event, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    LeaveRequest();
                }
            }));
        _tasks = new ExtensionTaskTracker(NotifyFailureAsync, logger);
        _contracts = new ExtensionContractRegistry(
            manifest.Exports,
            manifest.Imports,
            resolveProvider);
        var lifecycle = new ExtensionLifecycleApi(
            GetLifecycleStatus,
            cancellationToken => _reloadCallback is null
                ? ValueTask.FromResult(new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Unsupported,
                    GetLifecycleStatus(),
                    new ExtensionErrorDetail("Extension reload is unavailable because no reload callback is configured.")))
                : _reloadCallback(cancellationToken),
            cancellationToken => _unloadCallback is null
                ? ValueTask.FromResult(new ExtensionLifecycleOperationResult(
                    false,
                    ExtensionLifecycleOperationCode.Unsupported,
                    GetLifecycleStatus(),
                    new ExtensionErrorDetail("Extension unload is unavailable because no unload callback is configured.")))
                : _unloadCallback(cancellationToken));
        var capabilities = !ExtensionApiCapabilityGate.IsApi11Supported(hostApiVersion)
            ? UnsupportedExtensionCapabilities.Create(hostApiVersion)
            : ExtensionApiCapabilityGate.IsApi13Supported(hostApiVersion) &&
                capabilityFactory is IExtensionCapabilityFactoryRouteEvents routeFactory
                    ? routeFactory.CreateWithRouteEvents(manifest.Id, IsHandlerOwned, _routeRegistrations)
                    : capabilityFactory?.Create(manifest.Id, IsHandlerOwned)
                      ?? UnsupportedExtensionCapabilities.Create(hostApiVersion);
        _capabilityCleanup = capabilities.ServiceOutput as IAsyncDisposable;
        _serviceLogCapabilityCleanup = capabilities.ServiceOutput as IExtensionServiceLogCleanup;
        _runtimeStateCapabilityCleanup = capabilities.ServiceRuntimeState as IExtensionServiceRuntimeStateCleanup;
        _bridge = new ExtensionHostBridge(
            hostApiVersion,
            settings,
            _tasks,
            _events,
            _contracts,
            capabilities,
            lifecycle,
            ExtensionDependencyApi.Create(manifest, availableDependencyVersions, _contracts),
            ReportStatus,
            (_, _) => { },
            dataDirectory,
            logger);
        _entrypoint = loadHandle.CreateEntrypoint(_bridge);
    }

    private void ReportStatus(ExtensionStatus status)
    {
        ExtensionStatus? previous;
        lock (_gate)
        {
            previous = _reportedStatus;
            _reportedStatus = status;
        }

        if (_logger is not { } logger)
        {
            return;
        }

        if (status.Kind != ExtensionStatusKind.Healthy)
        {
            ExtensionLogMessages.ExtensionReportedUnhealthyStatus(
                logger,
                Manifest.Id,
                status.Kind.ToString(),
                status.Code);
        }
        else if (previous is { Kind: not ExtensionStatusKind.Healthy })
        {
            ExtensionLogMessages.ExtensionReportedHealthyStatus(logger, Manifest.Id);
        }
    }
    internal ExtensionRouteRegistrationSet RouteRegistrations => _routeRegistrations;


    internal ExtensionManifest Manifest { get; }

    internal IReadOnlyDictionary<string, IExtensionHandler> Handlers => _registry.Handlers;

    internal IReadOnlyDictionary<string, IExtensionStreamingHandler> StreamingHandlers => _registry.StreamingHandlers;

    internal IExtensionFallback? Fallback => _registry.Fallback;

    internal void SetFailureCallback(Func<Exception, ValueTask> callback) => _failureCallback = callback;

    internal void SetLifecycleCallbacks(
        Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>> reload,
        Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>> unload)
    {
        _reloadCallback = reload;
        _unloadCallback = unload;
    }
    internal void SetUnregisterCallbacks(Action<string> onHandlerUnregistered, Action onFallbackUnregistered) =>
        _registry.SetUnregisterCallbacks(onHandlerUnregistered, onFallbackUnregistered);
    internal bool IsHandlerOwned(string handlerId) =>
        ExtensionIdentifierSyntax.IsValid(handlerId) && _registry.IsHandlerAvailable(handlerId);

    internal bool IsStreamingHandler(string handlerId) =>
        ExtensionIdentifierSyntax.IsValid(handlerId) && _registry.IsStreamingHandler(handlerId);

    internal bool IsFallbackOwned => _registry.IsFallbackAvailable;
    internal ExtensionLifecycleStatus GetLifecycleStatus() =>
        ExtensionRuntimeManager.ToLifecycleStatus(GetStatus());
    internal async ValueTask<bool> StartAsync(
        bool reloading,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? timeoutSource = null;
        try
        {
            timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Lifecycle))
            {
                await _entrypoint!.StartAsync(
                        new ExtensionStartContext(reloading, _bridge, _contracts, _registry),
                        timeoutSource.Token)
                    .AsTask()
                    .WaitAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }

            if (_registry.RegistrationRejected)
            {
                lock (_gate)
                {
                    _lastFailure = ExtensionFailureCode.HandlerConflict;
                    _lastFailureDetail = _registry.RegistrationFailureDetail ??
                        new ExtensionErrorDetail("Extension registration was rejected because of a handler or fallback conflict.");
                }

                return false;
            }

            _contracts.CompleteStartup();
            return true;
        }
        catch (OperationCanceledException exception)
        {
            var timedOut = !cancellationToken.IsCancellationRequested &&
                timeoutSource?.IsCancellationRequested == true;
            var failureCode = timedOut
                ? ExtensionFailureCode.LifecycleFailed
                : ExtensionFailureCode.Cancelled;
            var operation = $"StartAsync(reloading: {reloading})";
            var failureMessage = timedOut
                ? $"Extension '{Manifest.Id}' {operation} timed out after {timeout} ({exception.GetType().Name})."
                : cancellationToken.IsCancellationRequested
                    ? $"Extension '{Manifest.Id}' {operation} was cancelled by the caller ({exception.GetType().Name})."
                    : $"Extension '{Manifest.Id}' {operation} was cancelled before completion ({exception.GetType().Name}).";
            lock (_gate)
            {
                _lastFailure = failureCode;
                _lastFailureDetail = new ExtensionErrorDetail(failureMessage);
            }

            return false;
        }
        catch (Exception exception)
        {
            await NotifyFailureAsync(exception).ConfigureAwait(false);
            // The failure notification records CallbackFailed; the start classification
            // (LifecycleFailed) must survive so candidate results carry the real cause.
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.LifecycleFailed;
                _lastFailureDetail = new ExtensionErrorDetail(
                    $"Extension '{Manifest.Id}' StartAsync(reloading: {reloading}) failed ({exception.GetType().Name}): {ExtensionDiagnosticText.Value(exception.Message)}.");
            }

            return false;
        }
        finally
        {
            timeoutSource?.Dispose();
        }
    }

    internal async ValueTask<bool> NotifyPreviousStoppedAsync(TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Lifecycle))
            {
                await _entrypoint!.OnPreviousStoppedAsync(timeoutSource.Token)
                    .AsTask()
                    .WaitAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception exception)
        {
            var timedOut = exception is OperationCanceledException && timeoutSource.IsCancellationRequested;
            await NotifyFailureAsync(exception).ConfigureAwait(false);
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.LifecycleFailed;
                _lastFailureDetail = timedOut
                    ? new ExtensionErrorDetail(
                        $"Extension '{Manifest.Id}' OnPreviousStoppedAsync timed out after {timeout} ({exception.GetType().Name}).")
                    : exception is OperationCanceledException
                        ? new ExtensionErrorDetail(
                            $"Extension '{Manifest.Id}' OnPreviousStoppedAsync was cancelled before completion ({exception.GetType().Name}).")
                        : new ExtensionErrorDetail(
                            $"Extension '{Manifest.Id}' OnPreviousStoppedAsync failed ({exception.GetType().Name}): {ExtensionDiagnosticText.Value(exception.Message)}.");
            }

            return false;
        }
    }

    internal async ValueTask<bool> StopForReplacementAsync(TimeSpan timeout)
    {
        Task<bool> stopTask;
        lock (_gate)
        {
            stopTask = _stopTask ??= StopCoreAsync(timeout);
        }

        return await stopTask.ConfigureAwait(false);
    }

    internal void MarkDraining()
    {
        lock (_gate)
        {
            if (_state is ExtensionLoadState.Loaded or ExtensionLoadState.Discovered)
            {
                _state = ExtensionLoadState.Unloading;
            }
        }
    }

    internal void MarkServing()
    {
        lock (_gate)
        {
            _state = ExtensionLoadState.Loaded;
        }
    }

    internal void ResumeServing() => MarkServing();

    /// <summary>Gets whether the one-way stop pipeline has started; such an instance can never serve again.</summary>
    internal bool StopStarted
    {
        get
        {
            lock (_gate)
            {
                return _stopTask is not null;
            }
        }
    }

    internal void MarkStopped()
    {
        lock (_gate)
        {
            _state = ExtensionLoadState.Stopped;
        }
    }

    internal void MarkFailed()
    {
        lock (_gate)
        {
            _state = ExtensionLoadState.Failed;
            _lastFailure = ExtensionFailureCode.FailureThresholdReached;
            _lastFailureDetail = new ExtensionErrorDetail(
                $"Extension '{Manifest.Id}' reached its rolling callback-failure threshold.");
        }
    }

    internal bool TryEnterRequest()
    {
        lock (_gate)
        {
            if (_state != ExtensionLoadState.Loaded)
            {
                return false;
            }

            _activeRequests++;
            return true;
        }
    }

    internal void LeaveRequest()
    {
        lock (_gate)
        {
            if (_activeRequests > 0)
            {
                _activeRequests--;
            }

            Monitor.PulseAll(_gate);
        }
    }

    internal bool RecordFailure(ExtensionFailureCode category, Exception exception)
    {
        var detail = ExtensionErrorDetail.FromException(exception);
        lock (_gate)
        {
            _lastFailure = category;
            _lastFailureDetail = detail;
        }

        return _failures.Record(DateTimeOffset.UtcNow);
    }
    private ValueTask RecordDroppedEvent(long droppedCount)
    {
        if (droppedCount > 0)
        {
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.EventQueueFull;
                _lastFailureDetail = new ExtensionErrorDetail(
                    $"The extension event queue dropped the newest event; {droppedCount} event(s) have been dropped.");
            }
        }

        return ValueTask.CompletedTask;
    }

    internal ExtensionRuntimeStatus GetStatus()
    {
        lock (_gate)
        {
            return new ExtensionRuntimeStatus(
                Manifest.Id,
                Manifest.Version.ToString(),
                _state,
                Handlers.Count,
                Fallback is not null,
                _activeRequests,
                _tasks.Count,
                _failures.Count,
                _events.DroppedCount,
                _lastFailure,
                _reportedStatus?.Kind,
                _reportedStatus?.Code,
                _lastFailureDetail);
        }
    }
    internal bool IsServing
    {
        get
        {
            lock (_gate)
            {
                return _state == ExtensionLoadState.Loaded;
            }
        }
    }

    internal bool TryPublishEvent(ExtensionEvent @event) =>
        IsServing && _events.TryPublish(@event).Succeeded;

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        AbortAsync(ExtensionRuntimeManager.LifecycleTimeout);

    internal async ValueTask AbortAsync(TimeSpan timeout)
    {
        MarkDraining();
        await StopForReplacementAsync(timeout).ConfigureAwait(false);
        await ReleaseAsync().ConfigureAwait(false);
    }

    internal ExtensionContractExportResolution TryResolveContract(string contractId, Type contractType, out object? value) =>
        _contracts.TryResolveExport(contractId, contractType, out value);

    /// <summary>Records one extension that imported a contract from this instance during this run.</summary>
    /// <param name="extensionId">The importing extension identifier.</param>
    internal void TrackContractConsumer(string extensionId)
    {
        lock (_gate)
        {
            _contractConsumers.Add(extensionId);
        }
    }

    /// <summary>Snapshots the extensions that imported contracts from this instance during this run.</summary>
    /// <returns>The recorded importing extension identifiers, in no particular order.</returns>
    internal string[] SnapshotContractConsumers()
    {
        lock (_gate)
        {
            return [.. _contractConsumers];
        }
    }

    /// <summary>Gets or creates the turnstile-isolated proxy handed out for one exported contract and consumer pair.</summary>
    /// <param name="contractId">The exported contract identifier.</param>
    /// <param name="consumerId">The importing extension identifier; each consumer gets its own proxy so provider-replacement rebinds re-record the right consumer.</param>
    /// <param name="contractType">The shared contract interface type.</param>
    /// <param name="target">This instance's contract implementation.</param>
    /// <param name="turnstile">This extension's dispatch turnstile.</param>
    /// <param name="resolver">Re-resolves the import after this instance is replaced.</param>
    /// <returns>The cached isolated contract reference.</returns>
    internal object GetOrCreateContractProxy(
        string contractId,
        string consumerId,
        Type contractType,
        object target,
        ExtensionDispatchTurnstile turnstile,
        Func<(ExtensionInstance Provider, object Target, ExtensionDispatchTurnstile Turnstile)?> resolver)
    {
        lock (_gate)
        {
            if (!_contractProxies.TryGetValue((contractId, consumerId), out var proxy))
            {
                proxy = ExtensionContractProxyFactory.Wrap(contractType, target, turnstile, this, resolver);
                _contractProxies[(contractId, consumerId)] = proxy;
            }

            return proxy;
        }
    }

    internal async ValueTask ReleaseAsync()
    {
        _routeRegistrations.Retire();
        // A merged service-output capability may own both cleanup contracts; dispose it once via log cleanup.
        if (_capabilityCleanup is { } capabilityCleanup &&
            !ReferenceEquals(capabilityCleanup, _serviceLogCapabilityCleanup))
        {
            try
            {
                await capabilityCleanup.DisposeAsync()
                    .AsTask()
                    .WaitAsync(CapabilityCleanupTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                if (_logger is { } timeoutLogger)
                {
                    ExtensionLogMessages.ExtensionServiceOutputCleanupTimedOut(
                        timeoutLogger,
                        exception,
                        Manifest.Id,
                        (int)CapabilityCleanupTimeout.TotalSeconds);
                }

                DetachCapabilityCleanup(capabilityCleanup);
            }
            catch (Exception exception)
            {
                if (_logger is { } failedLogger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        failedLogger,
                        exception,
                        Manifest.Id,
                        nameof(ReleaseAsync));
                }

                DetachCapabilityCleanup(capabilityCleanup);
            }
        }

        if (_runtimeStateCapabilityCleanup is { } runtimeStateCleanup)
        {
            try
            {
                await runtimeStateCleanup.DisposeAsync()
                    .AsTask()
                    .WaitAsync(CapabilityCleanupTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                if (_logger is { } timeoutLogger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        timeoutLogger,
                        exception,
                        Manifest.Id,
                        "ServiceRuntimeStateCleanup");
                }

                DetachRuntimeStateCleanup(runtimeStateCleanup);
            }
            catch (Exception exception)
            {
                if (_logger is { } failedLogger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        failedLogger,
                        exception,
                        Manifest.Id,
                        "ServiceRuntimeStateCleanup");
                }

                DetachRuntimeStateCleanup(runtimeStateCleanup);
            }
        }
        if (_serviceLogCapabilityCleanup is { } serviceLogCleanup)
        {
            try
            {
                await serviceLogCleanup.DisposeAsync()
                    .AsTask()
                    .WaitAsync(CapabilityCleanupTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                if (_logger is { } timeoutLogger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        timeoutLogger,
                        exception,
                        Manifest.Id,
                        "ServiceLogCleanup");
                }

                DetachServiceLogCleanup(serviceLogCleanup);
            }
            catch (Exception exception)
            {
                if (_logger is { } failedLogger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        failedLogger,
                        exception,
                        Manifest.Id,
                        "ServiceLogCleanup");
                }

                DetachServiceLogCleanup(serviceLogCleanup);
            }
        }

        _entrypoint = null;
        _registry.Clear();
        _contracts.Dispose();
        _loadHandle.Unload();
    }

    private void DetachCapabilityCleanup(IAsyncDisposable capabilityCleanup)
    {
        if (capabilityCleanup is not IExtensionServiceOutputCleanup detachable)
        {
            return;
        }

        try
        {
            detachable.DetachAll();
        }
        catch (Exception exception)
        {
            if (_logger is { } logger)
            {
                ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                    logger,
                    exception,
                    Manifest.Id,
                    nameof(IExtensionServiceOutputCleanup.DetachAll));
            }
        }
    }
    private void DetachRuntimeStateCleanup(IExtensionServiceRuntimeStateCleanup cleanup)
    {
        try
        {
            cleanup.DetachAll();
        }
        catch (Exception exception)
        {
            if (_logger is { } logger)
            {
                ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                    logger,
                    exception,
                    Manifest.Id,
                    nameof(IExtensionServiceRuntimeStateCleanup.DetachAll));
            }
        }
    }

    private void DetachServiceLogCleanup(IExtensionServiceLogCleanup cleanup)
    {
        try
        {
            cleanup.DetachAll();
        }
        catch (Exception exception)
        {
            if (_logger is { } logger)
            {
                ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                    logger,
                    exception,
                    Manifest.Id,
                    nameof(IExtensionServiceLogCleanup.DetachAll));
            }
        }
    }

    private async Task<bool> StopCoreAsync(TimeSpan timeout)
    {
        var drained = await WaitForDrainAsync(timeout).ConfigureAwait(false);
        var stopped = true;
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Lifecycle))
            {
                await _entrypoint!.StopAsync(timeoutSource.Token)
                    .AsTask()
                    .WaitAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            stopped = false;
            var timedOut = exception is OperationCanceledException && timeoutSource.IsCancellationRequested;
            await NotifyFailureAsync(exception).ConfigureAwait(false);
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.StopFailed;
                _lastFailureDetail = timedOut
                    ? new ExtensionErrorDetail(
                        $"Extension '{Manifest.Id}' StopAsync timed out after {timeout} ({exception.GetType().Name}).")
                    : exception is OperationCanceledException
                        ? new ExtensionErrorDetail(
                            $"Extension '{Manifest.Id}' StopAsync was cancelled before completion ({exception.GetType().Name}).")
                        : new ExtensionErrorDetail(
                            $"Extension '{Manifest.Id}' StopAsync failed ({exception.GetType().Name}): {ExtensionDiagnosticText.Value(exception.Message)}.");
            }
        }

        await _tasks.StopAsync(timeout).ConfigureAwait(false);
        await _events.DisposeAsync().ConfigureAwait(false);
        if (!drained)
        {
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.DrainTimeout;
                _lastFailureDetail = new ExtensionErrorDetail(
                    $"Extension '{Manifest.Id}' could not drain active requests during StopAsync within lifecycle timeout '{timeout}'.");
            }
        }

        return drained && stopped;
    }

    private async Task<bool> WaitForDrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_activeRequests > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_gate, remaining);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    private ValueTask NotifyFailureAsync(Exception exception)
    {
        var callback = _failureCallback;
        return callback is null ? ValueTask.CompletedTask : callback(exception);
    }
}
