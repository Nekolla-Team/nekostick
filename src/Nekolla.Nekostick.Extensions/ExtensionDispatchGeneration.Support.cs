using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

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

    /// <summary>Dispatches a streaming handler while this Host lease remains held.</summary>
    public async ValueTask<ExtensionStreamingInvocationResult> HandleStreamingAsync(
        string? handlerId,
        ExtensionStreamingRequest? request,
        CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        if (generation is null)
        {
            try
            {
                request?.BodyStream.Dispose();
            }
            catch
            {
            }

            return ExtensionStreamingInvocationResult.Unavailable;
        }

        return await generation.HandleStreamingWithLeaseAsync(handlerId, request, cancellationToken)
            .ConfigureAwait(false);
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
        : this(context, handler, null, fallback)
    {
    }

    internal ExtensionDispatchBinding(
        ExtensionDispatchContext context,
        IExtensionHandler? handler,
        IExtensionStreamingHandler? streamingHandler,
        IExtensionFallback? fallback)
    {
        Context = context;
        Handler = handler;
        StreamingHandler = streamingHandler;
        Fallback = fallback;
    }

    internal ExtensionDispatchContext Context { get; }

    internal IExtensionHandler? Handler { get; }

    internal IExtensionStreamingHandler? StreamingHandler { get; }

    internal IExtensionFallback? Fallback { get; }
}

internal sealed class ExtensionDispatchContext
{
    private readonly object _gate = new();
    internal ExtensionInstance Instance { get; }
    internal ExtensionSettingsConfiguration? Settings { get; }
    internal ExtensionRouteRegistrationSet? RouteRegistrations { get; }
    private int _generationReferences = 1;
    private bool _released;

    internal ExtensionDispatchContext(
        ExtensionInstance instance,
        ExtensionSettingsConfiguration? settings,
        ExtensionRouteRegistrationSet? routeRegistrations = null)
    {
        Instance = instance;
        Settings = settings;
        RouteRegistrations = routeRegistrations;
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
