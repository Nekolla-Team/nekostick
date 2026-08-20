using System.Collections.Immutable;
using System.Text.Json;
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
    private readonly ExtensionContractCatalog _contractCatalog;
    private readonly IExtensionCapabilityFactory? _capabilityFactory;
    private readonly Dictionary<string, ExtensionInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerBinding> _handlers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _dispatchLifetime = new();
    private HandlerBinding? _fallback;
    private bool _disposed;

    /// <summary>Creates an explicit-only runtime manager for one host API version and catalog.</summary>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    /// <param name="contractCatalog">The immutable host-owned shared contract catalog.</param>
    /// <param name="capabilityFactory">The optional host-owned factory for extension capabilities.</param>
    public ExtensionRuntimeManager(
        HostApiVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null,
        IExtensionCapabilityFactory? capabilityFactory = null)
    {
        _hostApiVersion = hostApiVersion;
        _contractCatalog = contractCatalog ?? ExtensionContractCatalog.CreateDefault();
        _capabilityFactory = capabilityFactory;
        _loader = new CollectibleExtensionLoader(
            new SemVersion(hostApiVersion.Major, hostApiVersion.Minor, hostApiVersion.Patch),
            _contractCatalog);
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
                    PublishExtensionState(previous, ExtensionLoadState.Unloading);
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
                PublishExtensionState(previous, ExtensionLoadState.Loaded);
                await candidate.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                return ExtensionRuntimeOperationResult.Failure(
                    ExtensionFailureCode.StopFailed,
                    previous.GetStatus());
            }

            if (!await candidate.NotifyPreviousStoppedAsync(LifecycleTimeout).ConfigureAwait(false))
            {
                previous.ResumeServing();
                PublishExtensionState(previous, ExtensionLoadState.Loaded);
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
                    PublishExtensionState(previous, ExtensionLoadState.Stopped);
                }
                else
                {
                    previous.ResumeServing();
                    PublishExtensionState(previous, ExtensionLoadState.Loaded);
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
                PublishExtensionState(instance, ExtensionLoadState.Unloading);
                _instances.Remove(extensionId);
            }

            var stopped = await instance.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            await instance.ReleaseAsync().ConfigureAwait(false);
            instance.MarkStopped();
            PublishExtensionState(instance, ExtensionLoadState.Stopped);
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


        if (binding is null || binding.Handler is not { } handler ||
            !binding.Instance.IsHandlerOwned(handlerId) ||
            !binding.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.Unavailable;
        }

        using var callbackScope = ExtensionCallbackGuard.Enter();

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

        if (binding is null || !binding.Instance.IsFallbackOwned || !binding.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.NotHandled;
        }

        using var callbackScope = ExtensionCallbackGuard.Enter();

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

        var contractFailure = ValidateManifestContracts(manifest);
        if (contractFailure != ExtensionFailureCode.None)
        {
            return CandidateResult.Failure(contractFailure);
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
                settings,
                ResolveContractProvider,
                _capabilityFactory);
            instance.SetFailureCallback(exception =>
                RecordFailureAsync(instance, ExtensionFailureCode.CallbackFailed, exception));
            instance.SetLifecycleCallbacks(
                cancellationToken => RequestReloadAsync(instance, cancellationToken),
                cancellationToken => RequestUnloadAsync(instance, cancellationToken));
            instance.SetUnregisterCallbacks(
                handlerId => RemoveHandlerRegistration(instance, handlerId),
                () => RemoveFallbackRegistration(instance));
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
    private async ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(

        ExtensionInstance instance,
        CancellationToken cancellationToken)
    {
        if (ExtensionCallbackGuard.IsActive)
        {
            return new(false, ExtensionLifecycleOperationCode.Reentrant, instance.GetLifecycleStatus());
        }

        try
        {
            var result = await ReloadAsync(instance.Manifest, instance.Settings, cancellationToken).ConfigureAwait(false);
            return ToLifecycleResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, ExtensionLifecycleOperationCode.Cancelled, instance.GetLifecycleStatus());
        }
        catch
        {
            return new(false, ExtensionLifecycleOperationCode.Failed, instance.GetLifecycleStatus());
        }
    }

    private async ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(
        ExtensionInstance instance,
        CancellationToken cancellationToken)
    {
        if (ExtensionCallbackGuard.IsActive)
        {
            return new(false, ExtensionLifecycleOperationCode.Reentrant, instance.GetLifecycleStatus());
        }

        try
        {
            var result = await UnloadAsync(instance.Manifest.Id, cancellationToken).ConfigureAwait(false);
            return ToLifecycleResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, ExtensionLifecycleOperationCode.Cancelled, instance.GetLifecycleStatus());
        }
        catch
        {
            return new(false, ExtensionLifecycleOperationCode.Failed, instance.GetLifecycleStatus());
        }
    }

    private static ExtensionLifecycleOperationResult ToLifecycleResult(ExtensionRuntimeOperationResult result) =>
        new(
            result.Succeeded,
            result.Succeeded
                ? ExtensionLifecycleOperationCode.Accepted
                : result.FailureCode switch
                {
                    ExtensionFailureCode.ExtensionNotLoaded => ExtensionLifecycleOperationCode.NotFound,
                    ExtensionFailureCode.AlreadyStopped => ExtensionLifecycleOperationCode.AlreadyStopped,
                    ExtensionFailureCode.Cancelled => ExtensionLifecycleOperationCode.Cancelled,
                    ExtensionFailureCode.HandlerConflict or ExtensionFailureCode.FallbackConflict => ExtensionLifecycleOperationCode.Conflict,
                    _ => ExtensionLifecycleOperationCode.Failed
                },
            result.Status is null ? null : ToLifecycleStatus(result.Status));

    private static ExtensionLifecycleStatus ToLifecycleStatus(ExtensionRuntimeStatus status) =>
        new(
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
                ExtensionFailureCode.InvalidArgument => ExtensionLifecycleFailureCode.InvalidArgument,
                ExtensionFailureCode.Cancelled => ExtensionLifecycleFailureCode.Cancelled,
                ExtensionFailureCode.AlreadyStopped => ExtensionLifecycleFailureCode.AlreadyStopped,
                ExtensionFailureCode.ExtensionNotLoaded => ExtensionLifecycleFailureCode.ExtensionNotLoaded,
                ExtensionFailureCode.LoadFailed => ExtensionLifecycleFailureCode.LoadFailed,
                ExtensionFailureCode.LifecycleFailed => ExtensionLifecycleFailureCode.LifecycleFailed,
                ExtensionFailureCode.StopFailed => ExtensionLifecycleFailureCode.StopFailed,
                ExtensionFailureCode.HandlerFailed => ExtensionLifecycleFailureCode.HandlerFailed,
                ExtensionFailureCode.CallbackFailed => ExtensionLifecycleFailureCode.CallbackFailed,
                ExtensionFailureCode.HandlerConflict or ExtensionFailureCode.FallbackConflict => ExtensionLifecycleFailureCode.RegistrationConflict,
                ExtensionFailureCode.ReplacementPreserved => ExtensionLifecycleFailureCode.ReplacementPreserved,
                _ => ExtensionLifecycleFailureCode.RuntimeUnavailable
            });


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
    private ExtensionFailureCode ValidateManifestContracts(ExtensionManifest manifest)
    {
        var imports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in manifest.Imports)
        {
            if (!imports.Add(import.ContractId))
            {
                return ExtensionFailureCode.DuplicateContractDeclaration;
            }

            var provider = manifest.Exports.FirstOrDefault(export =>
                string.Equals(export.ContractId, import.ContractId, StringComparison.Ordinal));
            if (provider is null && !TryFindContractProvider(import, out provider))
            {
                return ExtensionFailureCode.MissingContractProvider;
            }

            if (!import.VersionRange.IsSatisfiedBy(provider.Version))
            {
                return ExtensionFailureCode.ContractVersionIncompatible;
            }

            if (!string.Equals(import.AssemblyIdentity, provider.AssemblyIdentity, StringComparison.Ordinal) ||
                !string.Equals(import.TypeIdentity, provider.TypeIdentity, StringComparison.Ordinal))
            {
                return ExtensionFailureCode.ContractIdentityMismatch;
            }
        }

        return ExtensionFailureCode.None;
    }

    private bool TryFindContractProvider(
        ExtensionContractImport import,
        out ExtensionContractExport provider)
    {
        lock (_gate)
        {
            foreach (var instance in _instances.Values.Concat(_dispatchCandidates))
            {
                provider = instance.Manifest.Exports.FirstOrDefault(export =>
                    string.Equals(export.ContractId, import.ContractId, StringComparison.Ordinal))!;
                if (provider is not null)
                {
                    return true;
                }
            }
        }

        provider = null!;
        return false;
    }

    private object? ResolveContractProvider(string contractId, Type contractType)
    {
        lock (_gate)
        {
            foreach (var instance in _instances.Values.Concat(_dispatchCandidates))
            {
                if (instance.Manifest.Exports.Any(export =>
                        string.Equals(export.ContractId, contractId, StringComparison.Ordinal)) &&
                    instance.TryResolveContract(contractId, contractType, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>Publishes one required node-local core event without blocking the caller.</summary>
    /// <param name="event">The immutable core event.</param>
    /// <returns>The number of active extension queues that accepted the event.</returns>
    public int PublishCoreEvent(ExtensionCoreEvent? @event)
    {
        if (@event is null)
        {
            return 0;
        }

        var extensionEvent = new ExtensionEvent(
            @event.Kind.ToString(),
            @event.Version,
            @event.PayloadJson);
        ExtensionInstance[] recipients;
        lock (_gate)
        {
            recipients = _instances.Values
                .Where(static instance => instance.IsServing)
                .ToArray();
        }

        var accepted = 0;
        foreach (var recipient in recipients)
        {
            if (recipient.TryPublishEvent(extensionEvent))
            {
                accepted++;
            }
        }

        return accepted;
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
        PublishExtensionState(instance, ExtensionLoadState.Loaded);
    }
    private void PublishExtensionState(ExtensionInstance instance, ExtensionLoadState state)
    {
        try
        {
            var payloadJson = JsonSerializer.Serialize(new
            {
                extensionId = instance.Manifest.Id,
                version = instance.Manifest.Version.ToString(),
                state = state.ToString()
            });
            if (payloadJson.Length <= 4096)
            {
                PublishCoreEvent(new ExtensionCoreEvent(
                    ExtensionCoreEventKind.ExtensionStateChanged,
                    1,
                    payloadJson));
            }
        }
        catch (Exception)
        {
            // Lifecycle publication is best effort and must not alter extension transitions.
        }
    }

    private void RemoveHandlerRegistration(ExtensionInstance instance, string handlerId)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(handlerId, out var binding) && ReferenceEquals(binding.Instance, instance))
            {
                _handlers.Remove(handlerId);
            }
        }
    }

    private void RemoveFallbackRegistration(ExtensionInstance instance)
    {
        lock (_gate)
        {
            if (_fallback is not null && ReferenceEquals(_fallback.Instance, instance))
            {
                _fallback = null;
            }
        }
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
                    PublishExtensionState(instance, ExtensionLoadState.Failed);
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
    private IExtensionEntrypoint? _entrypoint;
    private Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>>? _reloadCallback;
    private Func<CancellationToken, ValueTask<ExtensionLifecycleOperationResult>>? _unloadCallback;
    private Task<bool>? _stopTask;
    private int _activeRequests;
    private readonly ExtensionTaskTracker _tasks;
    private readonly ExtensionEventQueue _events;
    private readonly ExtensionContractRegistry _contracts;
    private readonly ExtensionFailureTracker _failures = new();
    private Func<Exception, ValueTask>? _failureCallback;
    private readonly ExtensionHostBridge _bridge;
    private readonly ExtensionHandlerRegistry _registry = new();
    private ExtensionLoadState _state = ExtensionLoadState.Discovered;
    private ExtensionFailureCode _lastFailure;
    internal ExtensionInstance(
        ExtensionManifest manifest,
        ExtensionLoadHandle loadHandle,
        HostApiVersion hostApiVersion,
        ExtensionSettingsConfiguration? settings,
        Func<string, Type, object?> resolveProvider,
        IExtensionCapabilityFactory? capabilityFactory)
    {
        Manifest = manifest;
        Settings = settings;
        _loadHandle = loadHandle;
        _events = new ExtensionEventQueue(NotifyFailureAsync, onDrop: RecordDroppedEvent);
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
        var capabilities = capabilityFactory?.Create(manifest.Id, IsHandlerOwned)
            ?? UnsupportedExtensionCapabilities.Create();
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
