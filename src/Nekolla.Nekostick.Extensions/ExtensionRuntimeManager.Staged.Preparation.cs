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
    /// <param name="forceReloadIds">The extension identifiers that must be re-candidated even when their identity is unchanged.</param>
    /// <param name="cancellationToken">The preparation cancellation token.</param>
    /// <returns>A preparation result. Local binding failures are represented in the preparation status, not as a global failure.</returns>
    public async ValueTask<ExtensionGenerationPreparationResult> PrepareGenerationAsync(
        ImmutableArray<ExtensionRuntimeDescriptor> desired,
        ExtensionDispatchGeneration? previous = null,
        ImmutableHashSet<string>? forceReloadIds = null,
        CancellationToken cancellationToken = default)
    {
        var requestedForceReloadIds = forceReloadIds is null || forceReloadIds.IsEmpty
            ? ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)
            : forceReloadIds.WithComparer(StringComparer.Ordinal);
        if (cancellationToken.IsCancellationRequested)
        {
            return ExtensionGenerationPreparationResult.Failure(
                ExtensionFailureCode.Cancelled,
                new ExtensionErrorDetail("Extension generation preparation was canceled before it began."));
        }

        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _dispatchLifetime.Token);
        var operationToken = preparationCancellation.Token;
        try
        {
            await _dispatchGate.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return ExtensionGenerationPreparationResult.Failure(
                ExtensionFailureCode.Cancelled,
                ExtensionErrorDetail.FromException(exception));
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
                    return ExtensionGenerationPreparationResult.Failure(
                        ExtensionFailureCode.AlreadyStopped,
                        new ExtensionErrorDetail("The extension runtime manager has stopped."));
                }

                if (previous is not null && !ReferenceEquals(previous.Owner, this))
                {
                    return ExtensionGenerationPreparationResult.Failure(
                        ExtensionFailureCode.InvalidArgument,
                        new ExtensionErrorDetail("The supplied previous generation belongs to a different extension runtime manager."));
                }

                if (_publishedDispatchGeneration is not null &&
                    previous is not null &&
                    !ReferenceEquals(_publishedDispatchGeneration, previous))
                {
                    return ExtensionGenerationPreparationResult.Failure(
                        ExtensionFailureCode.RuntimeUnavailable,
                        new ExtensionErrorDetail("The supplied previous generation is not the currently published generation."));
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
                _contractCatalog,
                _logger);
            if (!graph.Succeeded)
            {
                return ExtensionGenerationPreparationResult.Failure(
                    graph.FailureCode,
                    graph.FailureDetail ?? new ExtensionErrorDetail(
                        $"The extension manifest graph could not be validated ({graph.FailureCode})."));
            }
            // These versions reflect desired manifests, not successful starts. The Host's failed-binding closure
            // prevents dependents from publishing when non-optional dependencies or contract imports fail.
            var availableDependencyVersions = graphManifests.ToDictionary(
                static manifest => manifest.Id,
                static manifest => manifest.Version,
                StringComparer.Ordinal);
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
                    var message = manifest is null
                        ? "The desired extension descriptor has no manifest."
                        : $"Settings for extension '{manifest.Id}' do not match the desired manifest.";
                    statuses.Add(CreateUnavailableStatus(
                        manifest?.Id,
                        manifest?.Version.ToString(),
                        requested,
                        ExtensionFailureCode.InvalidArgument,
                        new ExtensionErrorDetail(message)));
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
                        ExtensionFailureCode.RuntimeUnavailable,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' is requested more than once in the desired generation.")));
                    continue;
                }
                ExtensionDispatchContext? context = null;
                ExtensionInstance? candidate = null;
                var reused = false;
                var failureCode = ExtensionFailureCode.None;
                ExtensionErrorDetail? failureDetail = null;
                if (previousById.TryGetValue(manifest.Id, out var previousContext) &&
                    !requestedForceReloadIds.Contains(manifest.Id) &&
                    HasExactIdentity(previousContext, manifest, descriptor.Settings, descriptor.RouteIds, descriptor.ContentHash))
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
                            descriptor.RouteIds,
                            descriptor.ContentHash,
                            availableDependencyVersions)
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
                            started.RouteRegistrations,
                            descriptor.ContentHash,
                            _logger);
                        candidateContexts.Add(context);
                    }
                    else
                    {
                        failureCode = candidateResult.FailureCode == ExtensionFailureCode.None
                            ? ExtensionFailureCode.RuntimeUnavailable
                            : candidateResult.FailureCode;
                        failureDetail = candidateResult.FailureDetail ?? new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' could not be started ({failureCode}).");
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
                        failureCode,
                        failureDetail ?? new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' could not be made available in the desired generation.")));
                    continue;
                }

                var actualHandlers = context.Instance.Handlers;
                var actualStreamingHandlers = context.Instance.StreamingHandlers;
                var selectedIds = requested.IsDefaultOrEmpty
                    ? actualHandlers.Keys
                        .Concat(actualStreamingHandlers.Keys)
                        .Distinct(StringComparer.Ordinal)
                        .ToImmutableArray()
                    : requested;
                var unavailable = selectedIds
                    .Where(id => !actualHandlers.ContainsKey(id) && !actualStreamingHandlers.ContainsKey(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray();
                var collision = FindHandlerCollision(
                    selectedIds,
                    actualHandlers,
                    actualStreamingHandlers,
                    handlers);
                var wantsFallback = descriptor.IncludeFallback && context.Instance.Fallback is not null;
                if (collision != ExtensionFailureCode.None ||
                    (wantsFallback && fallback is not null))
                {
                    failureCode = collision != ExtensionFailureCode.None
                        ? collision
                        : ExtensionFailureCode.FallbackConflict;
                    failureDetail = failureCode == ExtensionFailureCode.FallbackConflict
                        ? new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' requests a fallback that conflicts with another generation binding.")
                        : new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' requests handlers that conflict with another generation binding.");
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
                        failureCode,
                        failureDetail!));
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
                    else if (actualStreamingHandlers.TryGetValue(handlerId, out var streamingHandler))
                    {
                        handlers.Add(handlerId, new ExtensionDispatchBinding(context, null, streamingHandler, null));
                    }
                }

                if (wantsFallback)
                {
                    fallback = new ExtensionDispatchBinding(context, null, context.Instance.Fallback);
                }

                var statusFailureCode = unavailable.Length == 0
                    ? ExtensionFailureCode.None
                    : ExtensionFailureCode.HandlerUnavailable;
                var statusFailureDetail = unavailable.Length == 0
                    ? null
                    : new ExtensionErrorDetail(
                        $"Extension '{manifest.Id}' does not provide requested handler(s): {string.Join(", ", unavailable)}.");
                statuses.Add(new ExtensionGenerationBindingStatus(
                    manifest.Id,
                    manifest.Version.ToString(),
                    selectedIds.Length > unavailable.Length || wantsFallback,
                    reused,
                    statusFailureCode,
                    requested,
                    unavailable,
                    wantsFallback,
                    statusFailureDetail));
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
                routeIdsByExtension,
                _logger,
                EnterGenerationDispatchAsync,
                GetPublishedGeneration,
                IsGenerationEntrySuspended,
                WaitForGenerationEntryResolutionAsync);
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
            if (_logger is { } preparationLogger)
            {
                var retainedCount = statuses.Count(static status => status.Reused);
                if (retainedCount > 0)
                {
                    var retainedList = string.Join(", ", statuses
                        .Where(static status => status.Reused)
                        .Select(static status => $"{status.ExtensionId}@{status.Version}")
                        .OrderBy(static value => value, StringComparer.Ordinal));
                    ExtensionLogMessages.ExtensionGenerationContextsRetained(
                        preparationLogger,
                        true,
                        retainedCount,
                        retainedList);
                }

                if (candidates.Count > 0)
                {
                    var startedList = string.Join(", ", candidates
                        .Select(static candidate => $"{candidate.Manifest.Id}@{candidate.Manifest.Version}")
                        .OrderBy(static value => value, StringComparer.Ordinal));
                    ExtensionLogMessages.ExtensionGenerationCandidatesStarted(
                        preparationLogger,
                        false,
                        candidates.Count,
                        startedList);
                }
            }

            keepGate = true;
            return ExtensionGenerationPreparationResult.Success(preparation);
        }
        catch (OperationCanceledException exception)
        {
            if (_logger is { } cancelledLogger)
            {
                ExtensionLogMessages.ExtensionGenerationPreparationCancelled(
                    cancelledLogger,
                    nameof(PrepareGenerationAsync));
            }

            await AbortUnpublishedAsync(generationContexts, candidateContexts, candidates).ConfigureAwait(false);
            return ExtensionGenerationPreparationResult.Failure(
                ExtensionFailureCode.Cancelled,
                ExtensionErrorDetail.FromException(exception));
        }
        catch (Exception exception)
        {
            if (_logger is { } failedLogger)
            {
                ExtensionLogMessages.ExtensionGenerationPreparationFailed(
                    failedLogger,
                    exception,
                    nameof(PrepareGenerationAsync));
            }

            await AbortUnpublishedAsync(generationContexts, candidateContexts, candidates).ConfigureAwait(false);
            return ExtensionGenerationPreparationResult.Failure(
                ExtensionFailureCode.RuntimeUnavailable,
                ExtensionErrorDetail.FromException(exception));
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
