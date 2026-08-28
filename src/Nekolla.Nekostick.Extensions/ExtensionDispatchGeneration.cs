using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Describes one explicitly desired extension runtime binding.</summary>
public sealed class ExtensionRuntimeDescriptor
{
    /// <summary>Creates a desired extension binding.</summary>
    /// <param name="manifest">The explicitly discovered immutable manifest.</param>
    /// <param name="settings">The immutable settings supplied for the manifest.</param>
    /// <param name="handlerIds">Optional route handler IDs to expose. An empty value exposes every registered handler.</param>
    /// <param name="includeFallback">Whether to expose the extension's fallback in the generation.</param>
    /// <param name="routeIds">Optional route IDs owned by this extension in the desired Host snapshot.</param>
    public ExtensionRuntimeDescriptor(
        ExtensionManifest? manifest,
        ExtensionSettingsConfiguration? settings = null,
        IEnumerable<string>? handlerIds = null,
        bool includeFallback = true,
        IEnumerable<Guid>? routeIds = null)
    {
        Manifest = manifest;
        Settings = settings;
        HandlerIds = handlerIds is null
            ? ImmutableArray<string>.Empty
            : handlerIds.ToImmutableArray();
        IncludeFallback = includeFallback;
        RouteIds = routeIds is null
            ? ImmutableArray<Guid>.Empty
            : routeIds.Distinct().ToImmutableArray();
    }

    /// <summary>Gets the route IDs owned by this extension in the desired Host snapshot.</summary>
    public ImmutableArray<Guid> RouteIds { get; }

    /// <summary>Gets the desired manifest, or <see langword="null" /> for a local unavailable binding.</summary>
    public ExtensionManifest? Manifest { get; }

    /// <summary>Gets the immutable settings desired for the manifest.</summary>
    public ExtensionSettingsConfiguration? Settings { get; }

    /// <summary>Gets the route handler IDs requested from this binding.</summary>
    public ImmutableArray<string> HandlerIds { get; }

    /// <summary>Gets whether the extension fallback is requested in the generation.</summary>
    public bool IncludeFallback { get; }
}

/// <summary>Reports one desired extension binding in a prepared dispatch generation.</summary>
public sealed record ExtensionGenerationBindingStatus
{
    internal ExtensionGenerationBindingStatus(
        string? extensionId,
        string? version,
        bool available,
        bool reused,
        ExtensionFailureCode failureCode,
        ImmutableArray<string> requestedHandlerIds,
        ImmutableArray<string> unavailableHandlerIds,
        bool fallbackAvailable)
    {
        ExtensionId = extensionId;
        Version = version;
        Available = available;
        Reused = reused;
        FailureCode = failureCode;
        RequestedHandlerIds = requestedHandlerIds;
        UnavailableHandlerIds = unavailableHandlerIds;
        FallbackAvailable = fallbackAvailable;
    }

    /// <summary>Gets the stable extension ID, when one was supplied.</summary>
    public string? ExtensionId { get; }

    /// <summary>Gets the desired manifest version text, when one was supplied.</summary>
    public string? Version { get; }

    /// <summary>Gets whether at least one requested binding is available.</summary>
    public bool Available { get; }

    /// <summary>Gets whether the live binding was retained without restarting.</summary>
    public bool Reused { get; }

    /// <summary>Gets the local binding failure category, if any.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the explicitly requested handler IDs.</summary>
    public ImmutableArray<string> RequestedHandlerIds { get; }

    /// <summary>Gets requested IDs unavailable in this generation.</summary>
    public ImmutableArray<string> UnavailableHandlerIds { get; }

    /// <summary>Gets whether this binding supplied the requested fallback.</summary>
    public bool FallbackAvailable { get; }
}

/// <summary>Reports the result of preparing an immutable extension dispatch generation.</summary>
public sealed class ExtensionGenerationPreparationResult
{
    private ExtensionGenerationPreparationResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionGenerationPreparation? preparation)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Preparation = preparation;
    }

    /// <summary>Gets whether a preparation object was created.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the global preparation failure category, if preparation was not created.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the preparation on success.</summary>
    public ExtensionGenerationPreparation? Preparation { get; }

    internal static ExtensionGenerationPreparationResult Success(ExtensionGenerationPreparation preparation) =>
        new(true, ExtensionFailureCode.None, preparation);

    internal static ExtensionGenerationPreparationResult Failure(ExtensionFailureCode failureCode) =>
        new(false, failureCode, null);
}

/// <summary>Reports the bounded changed-binding handoff before Host publication.</summary>
public sealed class ExtensionGenerationCommitResult
{
    private ExtensionGenerationCommitResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionDispatchGeneration? generation,
        ExtensionDispatchGeneration? previousGeneration)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Generation = generation;
        PreviousGeneration = previousGeneration;
    }

    /// <summary>Gets whether the generation reached the ready-to-publish state.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe handoff failure category, if any.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the immutable generation safe for Host holder exchange.</summary>
    public ExtensionDispatchGeneration? Generation { get; }

    /// <summary>Gets the generation that was serving before the handoff.</summary>
    public ExtensionDispatchGeneration? PreviousGeneration { get; }

    internal static ExtensionGenerationCommitResult Success(
        ExtensionDispatchGeneration generation,
        ExtensionDispatchGeneration? previousGeneration) =>
        new(true, ExtensionFailureCode.None, generation, previousGeneration);

    internal static ExtensionGenerationCommitResult Failure(
        ExtensionFailureCode failureCode,
        ExtensionDispatchGeneration? previousGeneration) =>
        new(false, failureCode, null, previousGeneration);
}

/// <summary>Represents one immutable, Host-owned extension dispatch generation.</summary>
public sealed partial class ExtensionDispatchGeneration : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ImmutableDictionary<string, ExtensionDispatchBinding> _handlers;
    private readonly ExtensionDispatchBinding? _fallback;
    private readonly ImmutableArray<ExtensionDispatchContext> _contexts;
    private readonly ImmutableArray<ExtensionGenerationBindingStatus> _bindings;
    private TaskCompletionSource<bool> _leasesDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _releaseTask;
    private bool _retirementRequested;
    private bool _released;
    private int _activeLeases;

    internal ExtensionDispatchGeneration(
        long generationId,
        ImmutableDictionary<string, ExtensionDispatchBinding> handlers,
        ExtensionDispatchBinding? fallback,
        IEnumerable<ExtensionDispatchContext> contexts,
        ImmutableArray<ExtensionGenerationBindingStatus> bindings,
        object owner,
        ImmutableDictionary<string, ImmutableArray<Guid>>? routeIdsByExtension = null)
    {
        GenerationId = generationId;
        _handlers = handlers;
        _fallback = fallback;
        _contexts = contexts.Distinct().ToImmutableArray();
        _bindings = bindings;
        Owner = owner;
        RouteIdsByExtension = routeIdsByExtension ?? ImmutableDictionary<string, ImmutableArray<Guid>>.Empty;
        InitializeRouteDispatch();
        if (_activeLeases == 0)
        {
            _leasesDrained.TrySetResult(true);
        }
    }

    /// <summary>Gets the monotonically increasing manager-local generation ID.</summary>
    public long GenerationId { get; }

    /// <summary>Gets immutable desired-binding status, including local unavailable IDs.</summary>
    public ImmutableArray<ExtensionGenerationBindingStatus> Bindings => _bindings;

    /// <summary>Gets every handler ID unavailable in this generation when explicitly requested.</summary>
    public ImmutableArray<string> UnavailableHandlerIds =>
        _bindings.SelectMany(static binding => binding.UnavailableHandlerIds).Distinct(StringComparer.Ordinal).ToImmutableArray();

    /// <summary>Attempts to acquire a Host request/publication lease for this generation.</summary>
    /// <returns>A lease, or <see langword="null" /> after retirement has begun.</returns>
    public ExtensionDispatchLease? TryAcquireLease()
    {
        lock (_gate)
        {
            if (_retirementRequested || _released)
            {
                return null;
            }

            if (_activeLeases == 0)
            {
                _leasesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _activeLeases++;
            return new ExtensionDispatchLease(this);
        }
    }

    /// <summary>Gets whether this generation has begun retirement.</summary>
    public bool IsRetiring
    {
        get
        {
            lock (_gate)
            {
                return _retirementRequested;
            }
        }
    }

    /// <summary>Atomically retains a context while this generation remains publishable.</summary>
    internal bool TryRetainContext(ExtensionDispatchContext context)
    {
        lock (_gate)
        {
            if (_retirementRequested || _released || !_contexts.Contains(context))
            {
                return false;
            }

            return context.TryRetainGeneration();
        }
    }

    /// <summary>Dispatches through a short-lived lease owned by this convenience call.</summary>
    public async ValueTask<ExtensionInvocationResult> HandleAsync(
        string? handlerId,
        ExtensionHandlerRequest? request,
        CancellationToken cancellationToken = default)
    {
        using var lease = TryAcquireLease();
        return lease is null
            ? ExtensionInvocationResult.Unavailable
            : await lease.HandleAsync(handlerId, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Dispatches fallback through a short-lived lease owned by this convenience call.</summary>
    public async ValueTask<ExtensionInvocationResult> HandleFallbackAsync(
        ExtensionHandlerRequest? request,
        ExtensionFallbackReason reason,
        CancellationToken cancellationToken = default)
    {
        using var lease = TryAcquireLease();
        return lease is null
            ? ExtensionInvocationResult.NotHandled
            : await lease.HandleFallbackAsync(request, reason, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ExtensionInvocationResult> HandleWithLeaseAsync(
        string? handlerId,
        ExtensionHandlerRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handlerId) || request is null ||
            !_handlers.TryGetValue(handlerId, out var binding) ||
            binding.Handler is not { } handler ||
            !binding.Context.Instance.IsHandlerOwned(handlerId) ||
            !binding.Context.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.Unavailable;
        }

        using var callbackScope = ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route);
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
            await binding.Context.RecordFailureAsync(ExtensionFailureCode.HandlerFailed, exception)
                .ConfigureAwait(false);
            return ExtensionInvocationResult.Failed;
        }
        finally
        {
            binding.Context.Instance.LeaveRequest();
        }
    }

    internal async ValueTask<ExtensionInvocationResult> HandleFallbackWithLeaseAsync(
        ExtensionHandlerRequest? request,
        ExtensionFallbackReason reason,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ExtensionInvocationResult.NotHandled;
        }

        var fallbackBinding = _fallback;
        if (fallbackBinding is null || fallbackBinding.Fallback is not { } fallback ||
            !fallbackBinding.Context.Instance.IsFallbackOwned ||
            !fallbackBinding.Context.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.NotHandled;
        }

        using var callbackScope = ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route);
        try
        {
            var result = await fallback.HandleAsync(
                    new ExtensionFallbackRequest(request, reason),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Handled && result.Response is not null
                ? ExtensionInvocationResult.Handled(result.Response)
                : ExtensionInvocationResult.NotHandled;
        }
        catch (Exception exception)
        {
            await fallbackBinding.Context.RecordFailureAsync(ExtensionFailureCode.CallbackFailed, exception)
                .ConfigureAwait(false);
            return ExtensionInvocationResult.Failed;
        }
        finally
        {
            fallbackBinding.Context.Instance.LeaveRequest();
        }
    }

    /// <summary>Returns IDs not present in this immutable generation.</summary>
    public ImmutableArray<string> GetUnavailableHandlerIds(IEnumerable<string>? handlerIds)
    {
        if (handlerIds is null)
        {
            return ImmutableArray<string>.Empty;
        }

        return handlerIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !_handlers.ContainsKey(id))
            .ToImmutableArray();
    }
    /// <summary>Retires this generation and waits for its active leases to drain.</summary>
    /// <param name="cancellationToken">The token that cancels the retirement wait.</param>
    /// <returns><see langword="true" /> if active leases drain and the generation is released; otherwise, <see langword="false" /> when the wait is canceled.</returns>
    public async ValueTask<bool> RetireAsync(CancellationToken cancellationToken = default)
    {
        CancelRouteDispatch();
        Task leasesDrained;
        lock (_gate)
        {
            _retirementRequested = true;
            if (_activeLeases == 0)
            {
                _leasesDrained.TrySetResult(true);
            }

            leasesDrained = _leasesDrained.Task;
        }

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ExtensionRuntimeManager.LifecycleTimeout);
            await leasesDrained.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = GetOrCreateReleaseTask(leasesDrained);
            return false;
        }

        await GetOrCreateReleaseTask(leasesDrained).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _ = await RetireAsync().ConfigureAwait(false);
    }

    private Task GetOrCreateReleaseTask(Task leasesDrained)
    {
        lock (_gate)
        {
            if (_releaseTask is not null)
            {
                return _releaseTask;
            }

            _releaseTask = ReleaseGenerationAfterDrainAsync(leasesDrained);
            _ = _releaseTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return _releaseTask;
        }
    }

    private async Task ReleaseGenerationAfterDrainAsync(Task leasesDrained)
    {
        await leasesDrained.ConfigureAwait(false);

        ExtensionDispatchContext[] contexts;
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            contexts = _contexts.ToArray();
        }

        foreach (var context in contexts)
        {
            await context.ReleaseGenerationAsync().ConfigureAwait(false);
        }

        DisposeRouteDispatch();
    }

    internal object Owner { get; }

    internal ImmutableDictionary<string, ImmutableArray<Guid>> RouteIdsByExtension { get; }

    internal IReadOnlyDictionary<string, ExtensionDispatchBinding> HandlerBindings => _handlers;

    internal ExtensionDispatchBinding? FallbackBinding => _fallback;

    internal ImmutableArray<ExtensionDispatchContext> Contexts => _contexts;

    internal void ReleaseLease()
    {
        lock (_gate)
        {
            if (_activeLeases > 0)
            {
                _activeLeases--;
            }

            if (_activeLeases == 0)
            {
                _leasesDrained.TrySetResult(true);
            }
        }
    }

    internal static ExtensionDispatchGeneration Empty(long generationId, object owner) =>
        new(
            generationId,
            ImmutableDictionary<string, ExtensionDispatchBinding>.Empty,
            null,
            Enumerable.Empty<ExtensionDispatchContext>(),
            ImmutableArray<ExtensionGenerationBindingStatus>.Empty,
            owner);
}
