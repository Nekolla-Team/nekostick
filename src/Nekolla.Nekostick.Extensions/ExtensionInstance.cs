using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal sealed partial class ExtensionInstance : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ExtensionLoadHandle _loadHandle;
    private readonly ExtensionHostBridge _bridge;
    private IExtensionEntrypoint? _entrypoint;
    private Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>>? _reloadCallback;
    private Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>>? _unloadCallback;
    private Func<Exception, ValueTask>? _failureCallback;
    private Task<bool>? _stopTask;
    private int _activeRequests;
    private readonly ExtensionTaskTracker _tasks;
    private readonly ExtensionEventQueue _events;
    private readonly ExtensionContractRegistry _contracts;
    private readonly ExtensionFailureTracker _failures = new();
    private readonly ExtensionHandlerRegistry _registry = new();
    private readonly ExtensionRouteRegistrationSet _routeRegistrations;
    private ExtensionLoadState _state = ExtensionLoadState.Discovered;
    private ExtensionFailureCode _lastFailure;
    internal ExtensionInstance(
        ExtensionManifest manifest,
        ExtensionLoadHandle loadHandle,
        HostApiVersion hostApiVersion,
        ExtensionSettingsConfiguration? settings,
        Func<string, Type, object?> resolveProvider,
        IExtensionCapabilityFactory? capabilityFactory,
        ImmutableArray<Guid> routeIds = default)
    {
        Manifest = manifest;
        Settings = settings;
        _loadHandle = loadHandle;
        _events = new ExtensionEventQueue(NotifyFailureAsync, onDrop: RecordDroppedEvent);
        _routeRegistrations = new ExtensionRouteRegistrationSet(
            manifest.Id,
            routeIds,
            callback => _events.TrySubscribe(callback));
        _tasks = new ExtensionTaskTracker(NotifyFailureAsync);
        _contracts = new ExtensionContractRegistry(
            manifest.Exports,
            manifest.Imports,
            resolveProvider);
        var lifecycle = new ExtensionLifecycleApi(
            GetLifecycleStatus,
            cancellationToken => _reloadCallback is null
                ? ValueTask.FromResult(new ExtensionLifecycleOperationResult(false, ExtensionLifecycleOperationCode.Unsupported, GetLifecycleStatus()))
                : _reloadCallback(cancellationToken),
            cancellationToken => _unloadCallback is null
                ? ValueTask.FromResult(new ExtensionLifecycleOperationResult(false, ExtensionLifecycleOperationCode.Unsupported, GetLifecycleStatus()))
                : _unloadCallback(cancellationToken));
        var capabilities = !ExtensionApiCapabilityGate.IsApi11Supported(hostApiVersion)
            ? UnsupportedExtensionCapabilities.Create(hostApiVersion)
            : ExtensionAbi.IsApi13Supported(hostApiVersion) &&
                capabilityFactory is IExtensionCapabilityFactoryRouteEvents routeFactory
                    ? routeFactory.CreateWithRouteEvents(manifest.Id, IsHandlerOwned, _routeRegistrations)
                    : capabilityFactory?.Create(manifest.Id, IsHandlerOwned)
                      ?? UnsupportedExtensionCapabilities.Create(hostApiVersion);
        _bridge = new ExtensionHostBridge(
            hostApiVersion,
            settings,
            _tasks,
            _events,
            _contracts,
            capabilities,
            lifecycle,
            _ => { },
            (_, _) => { });
        _entrypoint = loadHandle.CreateEntrypoint(_bridge);
    }
    internal ExtensionRouteRegistrationSet RouteRegistrations => _routeRegistrations;


    internal ExtensionManifest Manifest { get; }

    internal IReadOnlyDictionary<string, IExtensionHandler> Handlers => _registry.Handlers;

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

    internal bool IsFallbackOwned => _registry.IsFallbackAvailable;
    internal ExtensionLifecycleStatus GetLifecycleStatus()
    {
        var status = GetStatus();
        return new(
            status.ExtensionId,
            status.Version,
            status.State,
            status.HandlerCount,
            status.HasFallback,
            status.ActiveRequests,
            status.ActiveTasks,
            status.FailureCount,
            status.DroppedEvents,
            status.LastFailure switch
            {
                ExtensionFailureCode.None => ExtensionLifecycleFailureCode.None,
                ExtensionFailureCode.Cancelled => ExtensionLifecycleFailureCode.Cancelled,
                ExtensionFailureCode.AlreadyStopped => ExtensionLifecycleFailureCode.AlreadyStopped,
                ExtensionFailureCode.ExtensionNotLoaded => ExtensionLifecycleFailureCode.ExtensionNotLoaded,
                ExtensionFailureCode.LoadFailed => ExtensionLifecycleFailureCode.LoadFailed,
                ExtensionFailureCode.LifecycleFailed => ExtensionLifecycleFailureCode.LifecycleFailed,
                ExtensionFailureCode.StopFailed => ExtensionLifecycleFailureCode.StopFailed,
                ExtensionFailureCode.HandlerFailed => ExtensionLifecycleFailureCode.HandlerFailed,
                ExtensionFailureCode.CallbackFailed => ExtensionLifecycleFailureCode.CallbackFailed,
                ExtensionFailureCode.ReplacementPreserved => ExtensionLifecycleFailureCode.ReplacementPreserved,
                _ => ExtensionLifecycleFailureCode.RuntimeUnavailable
            });
    }
    internal async ValueTask<bool> StartAsync(
        bool reloading,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using (ExtensionCallbackGuard.Enter())
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
                _lastFailure = ExtensionFailureCode.HandlerConflict;
                return false;
            }

            _contracts.CompleteStartup();
            return true;
        }
        catch (OperationCanceledException)
        {
            _lastFailure = ExtensionFailureCode.Cancelled;
            return false;
        }
        catch (Exception exception)
        {
            _lastFailure = ExtensionFailureCode.LifecycleFailed;
            await NotifyFailureAsync(exception).ConfigureAwait(false);
            return false;
        }
    }

    internal async ValueTask<bool> NotifyPreviousStoppedAsync(TimeSpan timeout)
    {
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout);
            using (ExtensionCallbackGuard.Enter())
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
            _lastFailure = ExtensionFailureCode.LifecycleFailed;
            await NotifyFailureAsync(exception).ConfigureAwait(false);
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

    internal bool RecordFailure(ExtensionFailureCode category)
    {
        lock (_gate)
        {
            _lastFailure = category;
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
                _lastFailure);
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
        IsServing && _events.TryPublish(@event);

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        AbortAsync(ExtensionRuntimeManager.LifecycleTimeout);

    internal async ValueTask AbortAsync(TimeSpan timeout)
    {
        MarkDraining();
        await StopForReplacementAsync(timeout).ConfigureAwait(false);
        await ReleaseAsync().ConfigureAwait(false);
    }

    internal bool TryResolveContract(string contractId, Type contractType, out object? value) =>
        _contracts.TryResolveExport(contractId, contractType, out value);

    internal ValueTask ReleaseAsync()
    {
        _routeRegistrations.Retire();
        _entrypoint = null;
        _registry.Clear();
        _contracts.Dispose();
        _loadHandle.Unload();
        return ValueTask.CompletedTask;
    }

    private async Task<bool> StopCoreAsync(TimeSpan timeout)
    {
        var drained = await WaitForDrainAsync(timeout).ConfigureAwait(false);
        var stopped = true;
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout);
            using (ExtensionCallbackGuard.Enter())
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
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.StopFailed;
            }

            await NotifyFailureAsync(exception).ConfigureAwait(false);
        }

        await _tasks.StopAsync(timeout).ConfigureAwait(false);
        await _events.DisposeAsync().ConfigureAwait(false);
        if (!drained)
        {
            lock (_gate)
            {
                _lastFailure = ExtensionFailureCode.DrainTimeout;
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
