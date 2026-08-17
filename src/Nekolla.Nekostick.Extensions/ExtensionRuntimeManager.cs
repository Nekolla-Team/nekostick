using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Describes the safe outcome of one extension handler dispatch.</summary>
public enum ExtensionInvocationState
{
    /// <summary>The handler returned a response.</summary>
    Handled,

    /// <summary>No fallback or handler handled the request.</summary>
    NotHandled,

    /// <summary>The requested extension target is unavailable.</summary>
    Unavailable,

    /// <summary>The extension callback failed safely.</summary>
    Failed
}

/// <summary>Contains a framework-neutral handler dispatch result.</summary>
public sealed class ExtensionInvocationResult
{
    private ExtensionInvocationResult(ExtensionInvocationState state, ExtensionHandlerResponse? response)
    {
        State = state;
        Response = response;
    }

    /// <summary>Gets the dispatch outcome.</summary>
    public ExtensionInvocationState State { get; }

    /// <summary>Gets the response when the callback handled the request.</summary>
    public ExtensionHandlerResponse? Response { get; }

    /// <summary>Gets a safe unavailable result.</summary>
    public static ExtensionInvocationResult Unavailable { get; } =
        new(ExtensionInvocationState.Unavailable, null);

    /// <summary>Gets a safe not-handled result.</summary>
    public static ExtensionInvocationResult NotHandled { get; } =
        new(ExtensionInvocationState.NotHandled, null);

    internal static ExtensionInvocationResult Handled(ExtensionHandlerResponse response) =>
        new(ExtensionInvocationState.Handled, response);

    internal static ExtensionInvocationResult Failed =>
        new(ExtensionInvocationState.Failed, null);
}

/// <summary>Contains one safe extension runtime operation result.</summary>
public sealed class ExtensionRuntimeOperationResult
{
    private ExtensionRuntimeOperationResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionRuntimeStatus? status)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Status = status;
    }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the non-sensitive operation category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the resulting safe status when available.</summary>
    public ExtensionRuntimeStatus? Status { get; }

    internal static ExtensionRuntimeOperationResult Success(ExtensionRuntimeStatus status) =>
        new(true, ExtensionFailureCode.None, status);

    internal static ExtensionRuntimeOperationResult Failure(
        ExtensionFailureCode code,
        ExtensionRuntimeStatus? status = null) =>
        new(false, code, status);
}

/// <summary>Exposes safe observable state for one loaded extension.</summary>
public sealed record ExtensionRuntimeStatus
{
    /// <summary>Creates a safe runtime status.</summary>
    public ExtensionRuntimeStatus(
        string extensionId,
        string version,
        ExtensionLoadState state,
        int handlerCount,
        bool hasFallback,
        int activeRequests,
        int activeTasks,
        int failureCount,
        long droppedEvents,
        ExtensionFailureCode lastFailure)
    {
        ExtensionId = extensionId;
        Version = version;
        State = state;
        HandlerCount = handlerCount;
        HasFallback = hasFallback;
        ActiveRequests = activeRequests;
        ActiveTasks = activeTasks;
        FailureCount = failureCount;
        DroppedEvents = droppedEvents;
        LastFailure = lastFailure;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the loaded semantic version text.</summary>
    public string Version { get; }

    /// <summary>Gets the public extension state.</summary>
    public ExtensionLoadState State { get; }

    /// <summary>Gets the number of registered handlers.</summary>
    public int HandlerCount { get; }

    /// <summary>Gets whether this extension owns the fallback.</summary>
    public bool HasFallback { get; }

    /// <summary>Gets the number of active handler calls.</summary>
    public int ActiveRequests { get; }

    /// <summary>Gets the number of active tracked tasks.</summary>
    public int ActiveTasks { get; }

    /// <summary>Gets the number of failures in the rolling window.</summary>
    public int FailureCount { get; }

    /// <summary>Gets the number of newest events dropped by the bounded queue.</summary>
    public long DroppedEvents { get; }

    /// <summary>Gets the last safe failure category.</summary>
    public ExtensionFailureCode LastFailure { get; }
}

/// <summary>Runs explicit extension load, unload, reload, and handler operations.</summary>
public sealed partial class ExtensionRuntimeManager : IAsyncDisposable
{
    internal static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly CollectibleExtensionLoader _loader;
    private readonly HostApiVersion _hostApiVersion;
    private readonly Dictionary<string, ExtensionInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerBinding> _handlers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _dispatchLifetime = new();
    private HandlerBinding? _fallback;
    private bool _disposed;

    /// <summary>Creates an explicit-only runtime manager for one host API version.</summary>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    public ExtensionRuntimeManager(HostApiVersion hostApiVersion)
    {
        _hostApiVersion = hostApiVersion;
        _loader = new CollectibleExtensionLoader(new SemVersion(
            hostApiVersion.Major,
            hostApiVersion.Minor,
            hostApiVersion.Patch));
    }

    /// <summary>Loads and starts one explicitly supplied manifest.</summary>
    /// <param name="manifest">The previously discovered immutable manifest.</param>
    /// <param name="settings">The immutable Host snapshot settings for this extension.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe operation result.</returns>
    public async ValueTask<ExtensionRuntimeOperationResult> LoadAsync(
        ExtensionManifest? manifest,
        ExtensionSettingsConfiguration? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (manifest is null)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        try
        {
            await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.AlreadyStopped);
                }

                if (_publishedDispatchGeneration is not null || _instances.ContainsKey(manifest.Id))
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.RuntimeUnavailable);
                }
            }

            var candidate = await StartCandidateAsync(manifest, settings, reloading: false, cancellationToken)
                .ConfigureAwait(false);
            if (!candidate.Succeeded || candidate.Instance is null)
            {
                return ExtensionRuntimeOperationResult.Failure(candidate.FailureCode);
            }

            var instance = candidate.Instance;
            ExtensionFailureCode failureCode;
            lock (_gate)
            {
                if (_disposed || _publishedDispatchGeneration is not null || _instances.ContainsKey(manifest.Id))
                {
                    failureCode = ExtensionFailureCode.RuntimeUnavailable;
                }
                else
                {
                    failureCode = GetRegistrationConflict(instance, null);
                    if (failureCode == ExtensionFailureCode.None)
                    {
                        CommitInstance(instance);
                        return ExtensionRuntimeOperationResult.Success(instance.GetStatus());
                    }
                }
            }

            await instance.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
            return ExtensionRuntimeOperationResult.Failure(failureCode);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    /// <summary>Replaces one serving extension using start-before-switch ordering.</summary>
    /// <param name="replacement">The explicitly discovered replacement manifest.</param>
    /// <param name="settings">The immutable Host snapshot settings for the replacement.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe operation result that preserves the previous instance on candidate failure.</returns>
    public async ValueTask<ExtensionRuntimeOperationResult> ReloadAsync(
        ExtensionManifest? replacement,
        ExtensionSettingsConfiguration? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (replacement is null)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        try
        {
            await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        try
        {
            ExtensionInstance? previous;
            lock (_gate)
            {
                if (_disposed)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.AlreadyStopped);
                }

                if (_publishedDispatchGeneration is not null)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.RuntimeUnavailable);
                }

                if (!_instances.TryGetValue(replacement.Id, out previous) || previous is null)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.ExtensionNotLoaded);
                }
            }

            var candidateResult = await StartCandidateAsync(
                    replacement,
                    settings,
                    reloading: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!candidateResult.Succeeded || candidateResult.Instance is not { } candidate)
            {
                return ExtensionRuntimeOperationResult.Failure(
                    candidateResult.FailureCode == ExtensionFailureCode.None
                        ? ExtensionFailureCode.ReplacementPreserved
                        : candidateResult.FailureCode,
                    previous.GetStatus());
            }

            var candidateIsStale = false;
            lock (_gate)
            {
                if (!_instances.TryGetValue(replacement.Id, out var current) || !ReferenceEquals(current, previous))
                {
                    candidateIsStale = true;
                }
                else
                {
                    previous.MarkDraining();
                }
            }

            if (candidateIsStale)
            {
                await candidate.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                return ExtensionRuntimeOperationResult.Failure(
                    ExtensionFailureCode.ReplacementPreserved,
                    previous.GetStatus());
            }

            var oldStopped = await previous.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            if (!oldStopped)
            {
                previous.ResumeServing();
                await candidate.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                return ExtensionRuntimeOperationResult.Failure(
                    ExtensionFailureCode.StopFailed,
                    previous.GetStatus());
            }

            if (!await candidate.NotifyPreviousStoppedAsync(LifecycleTimeout).ConfigureAwait(false))
            {
                previous.ResumeServing();
                await candidate.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                return ExtensionRuntimeOperationResult.Failure(
                    ExtensionFailureCode.LifecycleFailed,
                    previous.GetStatus());
            }

            var conflict = ExtensionFailureCode.None;
            lock (_gate)
            {
                conflict = GetRegistrationConflict(candidate, previous);
                if (conflict == ExtensionFailureCode.None)
                {
                    RemoveInstanceRegistrations(previous);
                    CommitInstance(candidate);
                    previous.MarkStopped();
                }
                else
                {
                    previous.ResumeServing();
                }
            }

            if (conflict != ExtensionFailureCode.None)
            {
                await candidate.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                return ExtensionRuntimeOperationResult.Failure(conflict, previous.GetStatus());
            }

            await previous.ReleaseAsync().ConfigureAwait(false);
            return ExtensionRuntimeOperationResult.Success(candidate.GetStatus());
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    /// <summary>Stops and unloads one explicitly selected extension.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe operation result.</returns>
    public async ValueTask<ExtensionRuntimeOperationResult> UnloadAsync(
        string? extensionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        try
        {
            await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        try
        {
            ExtensionInstance? instance;
            lock (_gate)
            {
                if (_disposed)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.AlreadyStopped);
                }

                if (_publishedDispatchGeneration is not null)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.RuntimeUnavailable);
                }

                if (!_instances.TryGetValue(extensionId, out instance) || instance is null)
                {
                    return ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.ExtensionNotLoaded);
                }

                RemoveInstanceRegistrations(instance);
                instance.MarkDraining();
                _instances.Remove(extensionId);
            }

            var stopped = await instance.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            await instance.ReleaseAsync().ConfigureAwait(false);
            instance.MarkStopped();
            return stopped
                ? ExtensionRuntimeOperationResult.Success(instance.GetStatus())
                : ExtensionRuntimeOperationResult.Failure(ExtensionFailureCode.StopFailed, instance.GetStatus());
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    /// <summary>Dispatches one handler by stable ID without exposing runtime handles.</summary>
    /// <param name="handlerId">The stable handler ID.</param>
    /// <param name="request">The immutable request value.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A safe invocation result.</returns>
    public async ValueTask<ExtensionInvocationResult> HandleAsync(
        string? handlerId,
        ExtensionHandlerRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerId) || request is null)
        {
            return ExtensionInvocationResult.Unavailable;
        }

        HandlerBinding? binding;
        lock (_gate)
        {
            if (_activePreparation is not null || _publishedDispatchGeneration is not null)
            {
                return ExtensionInvocationResult.Unavailable;
            }

            _handlers.TryGetValue(handlerId, out binding);
        }


        if (binding is null || binding.Handler is not { } handler || !binding.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.Unavailable;
        }

        try
        {
            var response = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            return response is null
                ? ExtensionInvocationResult.Failed
                : ExtensionInvocationResult.Handled(response);
        }
        catch (OperationCanceledException)
        {
            return ExtensionInvocationResult.Failed;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(binding.Instance, ExtensionFailureCode.HandlerFailed, exception)
                .ConfigureAwait(false);
            return ExtensionInvocationResult.Failed;
        }
        finally
        {
            binding.Instance.LeaveRequest();
        }
    }

    /// <summary>Dispatches the sole fallback for a route no-match candidate.</summary>
    /// <param name="request">The immutable request value.</param>
    /// <param name="reason">The safe no-match reason.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A safe invocation result.</returns>
    public async ValueTask<ExtensionInvocationResult> HandleFallbackAsync(
        ExtensionHandlerRequest? request,
        ExtensionFallbackReason reason,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return ExtensionInvocationResult.NotHandled;
        }

        HandlerBinding? binding;
        lock (_gate)
        {
            if (_activePreparation is not null || _publishedDispatchGeneration is not null)
            {
                return ExtensionInvocationResult.NotHandled;
            }

            binding = _fallback;
        }

        if (binding is null || !binding.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.NotHandled;
        }

        try
        {
            var result = await binding.Fallback!.HandleAsync(
                    new ExtensionFallbackRequest(request, reason),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Handled && result.Response is not null
                ? ExtensionInvocationResult.Handled(result.Response)
                : ExtensionInvocationResult.NotHandled;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(binding.Instance, ExtensionFailureCode.CallbackFailed, exception)
                .ConfigureAwait(false);
            return ExtensionInvocationResult.Failed;
        }
        finally
        {
            binding.Instance.LeaveRequest();
        }
    }

    /// <summary>Gets a safe snapshot of all currently known extension states.</summary>
    public ImmutableArray<ExtensionRuntimeStatus> GetStatuses()
    {
        lock (_gate)
        {
            return _instances.Values.Select(static instance => instance.GetStatus()).ToImmutableArray();
        }
    }
    /// <summary>Gets one safe status by stable extension identifier.</summary>
    /// <param name="extensionId">The extension identifier.</param>
    /// <returns>The immutable status, or <see langword="null" /> when not loaded.</returns>
    public ExtensionRuntimeStatus? GetStatus(string? extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return null;
        }

        lock (_gate)
        {
            return _instances.TryGetValue(extensionId, out var instance)
                ? instance.GetStatus()
                : null;
        }
    }


    /// <summary>Stops all currently loaded extensions and releases their collectible contexts.</summary>
    public async ValueTask DisposeAsync()
    {
        _dispatchLifetime.Cancel();
        ExtensionGenerationPreparation? activePreparation;
        lock (_gate)
        {
            activePreparation = _activePreparation;
        }

        if (activePreparation is not null)
        {
            await activePreparation.AbortAsync().ConfigureAwait(false);
        }

        await _dispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ExtensionInstance[] directInstances;
            ExtensionDispatchGeneration? stagedGeneration;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                stagedGeneration = _publishedDispatchGeneration;
                _publishedDispatchGeneration = null;
                var generationInstances = stagedGeneration is null
                    ? new HashSet<ExtensionInstance>()
                    : stagedGeneration.Contexts
                        .Select(static context => context.Instance)
                        .ToHashSet();
                directInstances = _instances.Values
                    .Where(instance => !generationInstances.Contains(instance))
                    .ToArray();
                _dispatchCandidates.Clear();
                _instances.Clear();
                _handlers.Clear();
                _fallback = null;
                foreach (var instance in directInstances)
                {
                    instance.MarkDraining();
                }
            }

            if (stagedGeneration is not null)
            {
                _ = await stagedGeneration.RetireAsync().ConfigureAwait(false);
            }

            foreach (var instance in directInstances)
            {
                await instance.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
                await instance.ReleaseAsync().ConfigureAwait(false);
                instance.MarkStopped();
            }
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async ValueTask<CandidateResult> StartCandidateAsync(
        ExtensionManifest manifest,
        ExtensionSettingsConfiguration? settings,
        bool reloading,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CandidateResult.Failure(ExtensionFailureCode.Cancelled);
        }

        var loaded = _loader.Load(manifest);
        if (!loaded.Succeeded || loaded.Handle is null)
        {
            return CandidateResult.Failure(loaded.FailureCode);
        }

        ExtensionInstance? instance = null;
        try
        {
            instance = new ExtensionInstance(
                manifest,
                loaded.Handle,
                _hostApiVersion,
                settings);
            instance.SetSettings(settings);
            instance.SetFailureCallback(exception =>
                RecordFailureAsync(instance, ExtensionFailureCode.CallbackFailed, exception));
            if (!await instance.StartAsync(reloading, LifecycleTimeout, cancellationToken).ConfigureAwait(false))
            {
                await instance.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                return CandidateResult.Failure(ExtensionFailureCode.LifecycleFailed);
            }

            return CandidateResult.Success(instance);
        }
        catch (Exception)
        {
            if (instance is not null)
            {
                await instance.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
            }
            else
            {
                loaded.Handle.Unload();
            }

            return CandidateResult.Failure(ExtensionFailureCode.EntryConstructorFailed);
        }
    }

    private ExtensionFailureCode GetRegistrationConflict(ExtensionInstance candidate, ExtensionInstance? previous)
    {
        foreach (var handlerId in candidate.Handlers.Keys)
        {
            if (_handlers.TryGetValue(handlerId, out var binding) &&
                !ReferenceEquals(binding.Instance, previous))
            {
                return ExtensionFailureCode.HandlerConflict;
            }
        }

        if (candidate.Fallback is not null && _fallback is not null &&
            !ReferenceEquals(_fallback.Instance, previous))
        {
            return ExtensionFailureCode.FallbackConflict;
        }
        return ExtensionFailureCode.None;
    }

    private void CommitInstance(ExtensionInstance instance)
    {
        _instances[instance.Manifest.Id] = instance;
        foreach (var pair in instance.Handlers)
        {
            _handlers[pair.Key] = new HandlerBinding(instance, pair.Value, null);
        }

        if (instance.Fallback is not null)
        {
            _fallback = new HandlerBinding(instance, null, instance.Fallback);
        }

        instance.MarkServing();
    }

    private void RemoveInstanceRegistrations(ExtensionInstance instance)
    {
        foreach (var handlerId in instance.Handlers.Keys)
        {
            if (_handlers.TryGetValue(handlerId, out var binding) && ReferenceEquals(binding.Instance, instance))
            {
                _handlers.Remove(handlerId);
            }
        }

        if (_fallback is not null && ReferenceEquals(_fallback.Instance, instance))
        {
            _fallback = null;
        }
    }

    private async ValueTask RecordFailureAsync(
        ExtensionInstance? instance,
        ExtensionFailureCode category,
        Exception exception)
    {
        if (instance is null || instance.RecordFailure(category))
        {
            if (instance is not null)
            {
                _ = StopAfterFailureAsync(instance);
            }
        }

        await ValueTask.CompletedTask;
    }

    private async Task StopAfterFailureAsync(ExtensionInstance instance)
    {
        await _dispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var published = false;
            var shouldStop = false;
            lock (_gate)
            {
                if (_instances.TryGetValue(instance.Manifest.Id, out var current) &&
                    ReferenceEquals(current, instance))
                {
                    published = _publishedDispatchGeneration is not null;
                    instance.MarkFailed();
                    if (!published)
                    {
                        RemoveInstanceRegistrations(instance);
                        _instances.Remove(instance.Manifest.Id);
                    }

                    shouldStop = true;
                }
            }

            if (!shouldStop)
            {
                return;
            }

            await instance.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            if (!published)
            {
                await instance.ReleaseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private sealed record CandidateResult(bool Succeeded, ExtensionFailureCode FailureCode, ExtensionInstance? Instance)
    {
        internal static CandidateResult Success(ExtensionInstance instance) =>
            new(true, ExtensionFailureCode.None, instance);

        internal static CandidateResult Failure(ExtensionFailureCode code) =>
            new(false, code, null);
    }

    private sealed record HandlerBinding(
        ExtensionInstance Instance,
        IExtensionHandler? Handler,
        IExtensionFallback? Fallback);
}

internal sealed partial class ExtensionInstance : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ExtensionLoadHandle _loadHandle;
    private readonly ExtensionTaskTracker _tasks;
    private readonly ExtensionEventQueue _events;
    private readonly ExtensionFailureTracker _failures = new();
    private readonly ExtensionHostBridge _bridge;
    private readonly ExtensionHandlerRegistry _registry = new();
    private IExtensionEntrypoint? _entrypoint;
    private ExtensionLoadState _state = ExtensionLoadState.Discovered;
    private ExtensionFailureCode _lastFailure;
    private Func<Exception, ValueTask>? _failureCallback;
    private Task<bool>? _stopTask;
    private int _activeRequests;

    internal ExtensionInstance(
        ExtensionManifest manifest,
        ExtensionLoadHandle loadHandle,
        HostApiVersion hostApiVersion,
        ExtensionSettingsConfiguration? settings)
    {
        Manifest = manifest;
        _loadHandle = loadHandle;
        _events = new ExtensionEventQueue(NotifyFailureAsync);
        _tasks = new ExtensionTaskTracker(NotifyFailureAsync);
        _bridge = new ExtensionHostBridge(
            hostApiVersion,
            settings,
            _tasks,
            _events,
            _ => { },
            (_, _) => { });
        _entrypoint = loadHandle.CreateEntrypoint(_bridge);
    }

    internal ExtensionManifest Manifest { get; }

    internal IReadOnlyDictionary<string, IExtensionHandler> Handlers => _registry.Handlers;

    internal IExtensionFallback? Fallback => _registry.Fallback;

    internal void SetFailureCallback(Func<Exception, ValueTask> callback) => _failureCallback = callback;

    internal async ValueTask<bool> StartAsync(
        bool reloading,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await _entrypoint!.StartAsync(
                    new ExtensionStartContext(reloading, _bridge, _registry),
                    timeoutSource.Token)
                .AsTask()
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (_registry.RegistrationRejected)
            {
                _lastFailure = ExtensionFailureCode.HandlerConflict;
                return false;
            }

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
            await _entrypoint!.OnPreviousStoppedAsync(timeoutSource.Token)
                .AsTask()
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
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

    internal async ValueTask AbortAsync(TimeSpan timeout)
    {
        MarkDraining();
        await StopForReplacementAsync(timeout).ConfigureAwait(false);
        await ReleaseAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => AbortAsync(ExtensionRuntimeManager.LifecycleTimeout);

    internal ValueTask ReleaseAsync()
    {
        _entrypoint = null;
        _registry.Clear();
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
            await _entrypoint!.StopAsync(timeoutSource.Token)
                .AsTask()
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
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
