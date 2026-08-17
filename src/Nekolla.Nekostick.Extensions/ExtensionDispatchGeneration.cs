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
    public ExtensionRuntimeDescriptor(
        ExtensionManifest? manifest,
        ExtensionSettingsConfiguration? settings = null,
        IEnumerable<string>? handlerIds = null,
        bool includeFallback = true)
    {
        Manifest = manifest;
        Settings = settings;
        HandlerIds = handlerIds is null
            ? ImmutableArray<string>.Empty
            : handlerIds.ToImmutableArray();
        IncludeFallback = includeFallback;
    }

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
public sealed class ExtensionDispatchGeneration : IAsyncDisposable
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
        object owner)
    {
        GenerationId = generationId;
        _handlers = handlers;
        _fallback = fallback;
        _contexts = contexts.Distinct().ToImmutableArray();
        _bindings = bindings;
        Owner = owner;
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
            binding.Handler is not { } handler || !binding.Context.Instance.TryEnterRequest())
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
            !fallbackBinding.Context.Instance.TryEnterRequest())
        {
            return ExtensionInvocationResult.NotHandled;
        }

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
    /// <summary>Requests bounded retirement after Host has drained old snapshot leases.</summary>
    public async ValueTask<bool> RetireAsync(CancellationToken cancellationToken = default)
    {
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
    }


    internal object Owner { get; }

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

/// <summary>Tracks one Host lease over an immutable dispatch generation.</summary>
public sealed class ExtensionDispatchLease : IDisposable, IAsyncDisposable
{
    private ExtensionDispatchGeneration? _generation;

    internal ExtensionDispatchLease(ExtensionDispatchGeneration generation) => _generation = generation;

    /// <summary>Releases the Host lease.</summary>
    public void Dispose() => Interlocked.Exchange(ref _generation, null)?.ReleaseLease();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Dispatches a handler while this Host lease remains held.</summary>
    public ValueTask<ExtensionInvocationResult> HandleAsync(
        string? handlerId,
        ExtensionHandlerRequest? request,
        CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        return generation is null
            ? ValueTask.FromResult(ExtensionInvocationResult.Unavailable)
            : generation.HandleWithLeaseAsync(handlerId, request, cancellationToken);
    }

    /// <summary>Dispatches fallback while this Host lease remains held.</summary>
    public ValueTask<ExtensionInvocationResult> HandleFallbackAsync(
        ExtensionHandlerRequest? request,
        ExtensionFallbackReason reason,
        CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        return generation is null
            ? ValueTask.FromResult(ExtensionInvocationResult.NotHandled)
            : generation.HandleFallbackWithLeaseAsync(request, reason, cancellationToken);
    }
}

/// <summary>Owns one prepared generation until publication or abort.</summary>
public sealed class ExtensionGenerationPreparation : IAsyncDisposable
{
    private readonly ExtensionRuntimeManager _manager;
    private readonly ExtensionDispatchGeneration? _previous;
    private readonly ExtensionDispatchGeneration _generation;
    private readonly ImmutableArray<ExtensionInstance> _candidates;
    private readonly ImmutableArray<ExtensionInstance> _changedPrevious;
    private readonly ImmutableArray<ExtensionInstance> _detachedPrevious;
    private readonly ImmutableArray<ExtensionDispatchContext> _contexts;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _state;

    internal ExtensionGenerationPreparation(
        ExtensionRuntimeManager manager,
        ExtensionDispatchGeneration? previous,
        ExtensionDispatchGeneration generation,
        ImmutableArray<ExtensionInstance> candidates,
        ImmutableArray<ExtensionInstance> changedPrevious,
        ImmutableArray<ExtensionInstance> detachedPrevious,
        ImmutableArray<ExtensionDispatchContext> contexts)
    {
        _manager = manager;
        _previous = previous;
        _generation = generation;
        _candidates = candidates;
        _changedPrevious = changedPrevious;
        _detachedPrevious = detachedPrevious;
        _contexts = contexts;
    }

    /// <summary>Gets the immutable generation prepared for Host publication.</summary>
    public ExtensionDispatchGeneration Generation => _generation;

    /// <summary>Aborts candidates and restores the previous generation before publication.</summary>
    public async ValueTask AbortAsync()
    {
        _ = await _manager.AbortPreparationAsync(this).ConfigureAwait(false);
    }

    /// <summary>Performs changed-binding stop/drain and candidate activation without publishing.</summary>
    public ValueTask<ExtensionGenerationCommitResult> ReadyToPublishAsync(
        CancellationToken cancellationToken = default) =>
        _manager.ReadyToPublishAsync(this, cancellationToken);

    /// <summary>Marks this preparation published after Host atomically exchanges its holder.</summary>
    public ValueTask<bool> CompletePublicationAsync() =>
        _manager.CompletePublicationAsync(this);


    /// <inheritdoc />
    public ValueTask DisposeAsync() => AbortAsync();

    internal ExtensionDispatchGeneration? Previous => _previous;

    internal ImmutableArray<ExtensionInstance> Candidates => _candidates;

    internal ImmutableArray<ExtensionInstance> ChangedPrevious => _changedPrevious;

    internal ImmutableArray<ExtensionInstance> DetachedPrevious => _detachedPrevious;

    internal ImmutableArray<ExtensionDispatchContext> Contexts => _contexts;
    internal ValueTask EnterOperationAsync() => new(_operationGate.WaitAsync());

    internal void ExitOperation() => _operationGate.Release();

    internal bool TryTransition(int expected, int next) => Interlocked.CompareExchange(ref _state, next, expected) == expected;

    internal int State => Volatile.Read(ref _state);
}

internal sealed class ExtensionDispatchBinding
{
    internal ExtensionDispatchBinding(
        ExtensionDispatchContext context,
        IExtensionHandler? handler,
        IExtensionFallback? fallback)
    {
        Context = context;
        Handler = handler;
        Fallback = fallback;
    }

    internal ExtensionDispatchContext Context { get; }

    internal IExtensionHandler? Handler { get; }

    internal IExtensionFallback? Fallback { get; }
}

internal sealed class ExtensionDispatchContext
{
    private readonly object _gate = new();
    internal ExtensionInstance Instance { get; }
    internal ExtensionSettingsConfiguration? Settings { get; }
    private int _generationReferences = 1;
    private bool _released;

    internal ExtensionDispatchContext(
        ExtensionInstance instance,
        ExtensionSettingsConfiguration? settings)
    {
        Instance = instance;
        Settings = settings;
    }
    internal bool TryRetainGeneration()
    {
        lock (_gate)
        {
            if (_released)
            {
                return false;
            }

            _generationReferences++;
            return true;
        }
    }

    internal void RetainGeneration()
    {
        if (!TryRetainGeneration())
        {
            throw new InvalidOperationException("The extension dispatch context was released.");
        }
    }

    internal async ValueTask ReleaseGenerationAsync()
    {
        var release = false;
        lock (_gate)
        {
            if (_generationReferences > 0)
            {
                _generationReferences--;
            }

            if (_generationReferences == 0 && !_released)
            {
                _released = true;
                release = true;
            }
        }

        if (release)
        {
            await Instance.StopForReplacementAsync(ExtensionRuntimeManager.LifecycleTimeout)
                .ConfigureAwait(false);
            await Instance.ReleaseAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask AbortCandidateAsync()
    {
        var release = false;
        lock (_gate)
        {
            if (!_released)
            {
                _released = true;
                _generationReferences = 0;
                release = true;
            }
        }

        if (release)
        {
            await Instance.AbortAsync(ExtensionRuntimeManager.LifecycleTimeout).ConfigureAwait(false);
        }
    }

    internal ValueTask RecordFailureAsync(ExtensionFailureCode category, Exception exception) =>
        Instance.NotifyExternalFailureAsync(category, exception);
}
