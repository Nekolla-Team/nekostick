using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

public sealed partial class ExtensionRuntimeManager
{
    /// <summary>
    /// Prepares an immutable dispatch generation from explicit desired manifests and settings.
    /// Candidate instances are started but are not serving or published until
    /// <see cref="ExtensionGenerationPreparation.ReadyToPublishAsync"/> completes.
    /// </summary>
    /// <param name="desired">The explicit desired extension descriptors.</param>
    /// <param name="previous">The generation currently held by Host, when available.</param>
    /// <param name="cancellationToken">The preparation cancellation token.</param>
    /// <returns>A preparation result. Local binding failures are represented in the preparation status, not as a global failure.</returns>
    public async ValueTask<ExtensionGenerationPreparationResult> PrepareGenerationAsync(
        ImmutableArray<ExtensionRuntimeDescriptor> desired,
        ExtensionDispatchGeneration? previous = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _dispatchLifetime.Token);
        var operationToken = preparationCancellation.Token;
        try
        {
            await _dispatchGate.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.Cancelled);
        }

        var keepGate = false;
        var generationContexts = new List<ExtensionDispatchContext>();
        var candidateContexts = new List<ExtensionDispatchContext>();
        var candidates = new List<ExtensionInstance>();
        try
        {
            ExtensionDispatchGeneration baseGeneration;
            lock (_gate)
            {
                if (_disposed)
                {
                    return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.AlreadyStopped);
                }

                if (previous is not null && !ReferenceEquals(previous.Owner, this))
                {
                    return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.InvalidArgument);
                }

                if (_publishedDispatchGeneration is not null &&
                    previous is not null &&
                    !ReferenceEquals(_publishedDispatchGeneration, previous))
                {
                    return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.RuntimeUnavailable);
                }

                baseGeneration = previous ?? _publishedDispatchGeneration ??
                    ExtensionDispatchGeneration.Empty(
                        Interlocked.Increment(ref _nextDispatchGenerationId),
                        this);
            }

            var baseInstances = baseGeneration.Contexts
                .Select(static context => context.Instance)
                .ToHashSet();
            var detachedPrevious = new List<ExtensionInstance>();
            lock (_gate)
            {
                foreach (var instance in _instances.Values)
                {
                    if (!baseInstances.Contains(instance))
                    {
                        detachedPrevious.Add(instance);
                    }
                }
            }

            var descriptors = desired.IsDefault
                ? ImmutableArray<ExtensionRuntimeDescriptor>.Empty
                : desired;
            var graphManifests = descriptors
                .Where(static descriptor => descriptor?.Manifest is not null)
                .Select(static descriptor => descriptor.Manifest!)
                .ToImmutableArray();
            var graph = ExtensionManifestGraph.ValidateAndOrder(
                graphManifests,
                new SemVersion(_hostApiVersion.Major, _hostApiVersion.Minor, _hostApiVersion.Patch),
                _contractCatalog);
            if (!graph.Succeeded)
            {
                return ExtensionGenerationPreparationResult.Failure(graph.FailureCode);
            }
            if (graph.OrderedManifests.Length == descriptors.Length)
            {
                var descriptorsById = descriptors.ToDictionary(
                    static descriptor => descriptor.Manifest!.Id,
                    StringComparer.Ordinal);
                descriptors = graph.OrderedManifests
                    .Select(manifest => descriptorsById[manifest.Id])
                    .ToImmutableArray();
            }

            var previousById = baseGeneration.Contexts
                .GroupBy(static context => context.Instance.Manifest.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            var desiredIds = new HashSet<string>(StringComparer.Ordinal);
            var handlers = new Dictionary<string, ExtensionDispatchBinding>(StringComparer.Ordinal);
            var fallback = default(ExtensionDispatchBinding);
            var contexts = new List<ExtensionDispatchContext>();
            var changedPrevious = new List<ExtensionInstance>();
            var statuses = new List<ExtensionGenerationBindingStatus>();
            var candidateById = new Dictionary<string, ExtensionInstance>(StringComparer.Ordinal);

            foreach (var detached in detachedPrevious)
            {
                AddChangedPrevious(changedPrevious, detached);
            }
            foreach (var descriptor in descriptors)
            {
                operationToken.ThrowIfCancellationRequested();
                if (descriptor is null)
                {
                    continue;
                }

                var manifest = descriptor.Manifest;
                var requested = NormalizeHandlerIds(descriptor.HandlerIds);
                if (manifest is null ||
                    (descriptor.Settings is not null &&
                     !string.Equals(descriptor.Settings.ExtensionId, manifest.Id, StringComparison.Ordinal)))
                {
                    statuses.Add(CreateUnavailableStatus(
                        manifest?.Id,
                        manifest?.Version.ToString(),
                        requested,
                        ExtensionFailureCode.InvalidArgument));
                    continue;
                }

                if (!desiredIds.Add(manifest.Id))
                {
                    if (previousById.TryGetValue(manifest.Id, out var duplicatePrevious))
                    {
                        AddChangedPrevious(changedPrevious, duplicatePrevious.Instance);
                    }

                    statuses.Add(CreateUnavailableStatus(
                        manifest.Id,
                        manifest.Version.ToString(),
                        requested,
                        ExtensionFailureCode.RuntimeUnavailable));
                    continue;
                }

                ExtensionDispatchContext? context = null;
                ExtensionInstance? candidate = null;
                var reused = false;
                var failureCode = ExtensionFailureCode.None;
                if (previousById.TryGetValue(manifest.Id, out var previousContext) &&
                    HasExactIdentity(previousContext, manifest, descriptor.Settings, descriptor.RouteIds))
                {
                    if (!baseGeneration.TryRetainContext(previousContext))
                    {
                        throw new InvalidOperationException("The prepared generation is retiring.");
                    }

                    context = previousContext;
                    reused = true;
                }
                else
                {
                    var candidateResult = await StartCandidateAsync(
                            manifest,
                            descriptor.Settings,
                            reloading: true,
                            operationToken,
                            descriptor.RouteIds)
                        .ConfigureAwait(false);
                    if (candidateResult.Succeeded && candidateResult.Instance is { } started)
                    {
                        candidate = started;
                        lock (_gate)
                        {
                            _dispatchCandidates.Add(started);
                        }

                        candidates.Add(started);
                        candidateById[manifest.Id] = started;
                        context = new ExtensionDispatchContext(
                            started,
                            descriptor.Settings,
                            started.RouteRegistrations);
                        candidateContexts.Add(context);
                    }
                    else
                    {
                        failureCode = candidateResult.FailureCode == ExtensionFailureCode.None
                            ? ExtensionFailureCode.RuntimeUnavailable
                            : candidateResult.FailureCode;
                    }
                }

                if (context is null)
                {
                    if (previousById.TryGetValue(manifest.Id, out var failedPrevious))
                    {
                        AddChangedPrevious(changedPrevious, failedPrevious.Instance);
                    }

                    statuses.Add(CreateUnavailableStatus(
                        manifest.Id,
                        manifest.Version.ToString(),
                        requested,
                        failureCode));
                    continue;
                }

                var actualHandlers = context.Instance.Handlers;
                var selectedIds = requested.IsDefaultOrEmpty
                    ? actualHandlers.Keys.ToImmutableArray()
                    : requested;
                var unavailable = selectedIds
                    .Where(id => !actualHandlers.ContainsKey(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray();
                var collision = FindHandlerCollision(selectedIds, actualHandlers, handlers);
                var wantsFallback = descriptor.IncludeFallback && context.Instance.Fallback is not null;
                if (collision != ExtensionFailureCode.None ||
                    (wantsFallback && fallback is not null))
                {
                    failureCode = collision != ExtensionFailureCode.None
                        ? collision
                        : ExtensionFailureCode.FallbackConflict;
                    if (candidate is not null)
                    {
                        await AbortCandidateAsync(candidate).ConfigureAwait(false);
                        candidateContexts.Remove(context);
                        candidates.Remove(candidate);
                        candidateById.Remove(manifest.Id);
                    }
                    else
                    {
                        await context.ReleaseGenerationAsync().ConfigureAwait(false);
                    }

                    if (previousById.TryGetValue(manifest.Id, out var conflictedPrevious))
                    {
                        AddChangedPrevious(changedPrevious, conflictedPrevious.Instance);
                    }

                    statuses.Add(CreateUnavailableStatus(
                        manifest.Id,
                        manifest.Version.ToString(),
                        requested,
                        failureCode));
                    continue;
                }

                contexts.Add(context);
                generationContexts.Add(context);
                foreach (var handlerId in selectedIds.Distinct(StringComparer.Ordinal))
                {
                    if (actualHandlers.TryGetValue(handlerId, out var handler))
                    {
                        handlers.Add(handlerId, new ExtensionDispatchBinding(context, handler, null));
                    }
                }

                if (wantsFallback)
                {
                    fallback = new ExtensionDispatchBinding(context, null, context.Instance.Fallback);
                }

                statuses.Add(new ExtensionGenerationBindingStatus(
                    manifest.Id,
                    manifest.Version.ToString(),
                    selectedIds.Length > unavailable.Length || wantsFallback,
                    reused,
                    unavailable.Length == 0 ? ExtensionFailureCode.None : ExtensionFailureCode.HandlerUnavailable,
                    requested,
                    unavailable,
                    wantsFallback));
            }

            foreach (var previousContext in previousById.Values)
            {
                if (!desiredIds.Contains(previousContext.Instance.Manifest.Id) ||
                    !contexts.Any(context => ReferenceEquals(context, previousContext)))
                {
                    AddChangedPrevious(changedPrevious, previousContext.Instance);
                }
            }

            operationToken.ThrowIfCancellationRequested();
            var routeIdsByExtension = descriptors
                .Where(static descriptor => descriptor.Manifest is not null && descriptor.RouteIds.Any())
                .ToImmutableDictionary(
                    static descriptor => descriptor.Manifest!.Id,
                    static descriptor => descriptor.RouteIds,
                    StringComparer.Ordinal);
            var generation = new ExtensionDispatchGeneration(
                Interlocked.Increment(ref _nextDispatchGenerationId),
                handlers.ToImmutableDictionary(StringComparer.Ordinal),
                fallback,
                contexts,
                statuses.ToImmutableArray(),
                this,
                routeIdsByExtension);
            var handoffPrevious = changedPrevious
                .Where(previousContext => candidateById.ContainsKey(previousContext.Manifest.Id))
                .ToImmutableArray();
            var preparation = new ExtensionGenerationPreparation(
                this,
                baseGeneration,
                generation,
                candidates.ToImmutableArray(),
                handoffPrevious,
                detachedPrevious.ToImmutableArray(),
                generationContexts.ToImmutableArray());
            lock (_gate)
            {
                _activePreparation = preparation;
            }

            keepGate = true;
            return ExtensionGenerationPreparationResult.Success(preparation);
        }
        catch (OperationCanceledException)
        {
            await AbortUnpublishedAsync(generationContexts, candidateContexts, candidates).ConfigureAwait(false);
            return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.Cancelled);
        }
        catch (Exception)
        {
            await AbortUnpublishedAsync(generationContexts, candidateContexts, candidates).ConfigureAwait(false);
            return ExtensionGenerationPreparationResult.Failure(ExtensionFailureCode.RuntimeUnavailable);
        }
        finally
        {
            if (!keepGate)
            {
                _dispatchGate.Release();
            }
        }
    }
}
