using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

public sealed partial class ExtensionRuntimeManager
{
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly HashSet<ExtensionInstance> _dispatchCandidates = new();
    private ExtensionGenerationPreparation? _activePreparation;
    private ExtensionDispatchGeneration? _publishedDispatchGeneration;
    private long _nextDispatchGenerationId;

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
                    HasExactIdentity(previousContext, manifest, descriptor.Settings))
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
                            operationToken)
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
                        context = new ExtensionDispatchContext(started, descriptor.Settings);
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
            var generation = new ExtensionDispatchGeneration(
                Interlocked.Increment(ref _nextDispatchGenerationId),
                handlers.ToImmutableDictionary(StringComparer.Ordinal),
                fallback,
                contexts,
                statuses.ToImmutableArray(),
                this);
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

    internal async ValueTask<ExtensionGenerationCommitResult> ReadyToPublishAsync(
        ExtensionGenerationPreparation preparation,
        CancellationToken cancellationToken)
    {
        await preparation.EnterOperationAsync().ConfigureAwait(false);
        try
        {
            if (preparation.State == 2)
            {
                return ExtensionGenerationCommitResult.Success(preparation.Generation, preparation.Previous);
            }

            if (preparation.State != 0 || !preparation.TryTransition(0, 1))
            {
                return ExtensionGenerationCommitResult.Failure(
                    preparation.State == 2 ? ExtensionFailureCode.None : ExtensionFailureCode.RuntimeUnavailable,
                    preparation.Previous);
            }

            using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _dispatchLifetime.Token);
            var operationToken = readinessCancellation.Token;
            try
            {
                lock (_gate)
                {
                    if (_disposed ||
                        (preparation.Previous is null
                            ? _publishedDispatchGeneration is not null
                            : _publishedDispatchGeneration is not null &&
                              !ReferenceEquals(_publishedDispatchGeneration, preparation.Previous)))
                    {
                        throw new InvalidOperationException("The prepared generation is stale.");
                    }
                }

                foreach (var previous in preparation.ChangedPrevious)
                {
                    operationToken.ThrowIfCancellationRequested();
                    previous.MarkDraining();
                }

                foreach (var previous in preparation.ChangedPrevious)
                {
                    operationToken.ThrowIfCancellationRequested();
                    if (!await previous.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false))
                    {
                        await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                        return ExtensionGenerationCommitResult.Failure(
                            ExtensionFailureCode.StopFailed,
                            preparation.Previous);
                    }
                }

                var changedIds = preparation.ChangedPrevious
                    .Select(static previous => previous.Manifest.Id)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var candidate in preparation.Candidates)
                {
                    operationToken.ThrowIfCancellationRequested();
                    if (changedIds.Contains(candidate.Manifest.Id) &&
                        !await candidate.NotifyPreviousStoppedAsync(LifecycleTimeout).ConfigureAwait(false))
                    {
                        await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                        return ExtensionGenerationCommitResult.Failure(
                            ExtensionFailureCode.LifecycleFailed,
                            preparation.Previous);
                    }
                }

                foreach (var candidate in preparation.Candidates)
                {
                    operationToken.ThrowIfCancellationRequested();
                    candidate.MarkServing();
                }

                return ExtensionGenerationCommitResult.Success(preparation.Generation, preparation.Previous);
            }
            catch (OperationCanceledException)
            {
                await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                return ExtensionGenerationCommitResult.Failure(
                    ExtensionFailureCode.Cancelled,
                    preparation.Previous);
            }
            catch (Exception)
            {
                await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                return ExtensionGenerationCommitResult.Failure(
                    ExtensionFailureCode.RuntimeUnavailable,
                    preparation.Previous);
            }
        }
        finally
        {
            preparation.ExitOperation();
        }
    }

    internal async ValueTask<bool> CompletePublicationAsync(ExtensionGenerationPreparation preparation)
    {
        if (preparation.State == 2)
        {
            return true;
        }

        await preparation.EnterOperationAsync().ConfigureAwait(false);
        var releaseDispatchGate = false;
        try
        {
            if (preparation.State == 2)
            {
                return true;
            }

            if (preparation.State != 1)
            {
                return false;
            }

            releaseDispatchGate = true;
            return await CompletePublicationCoreAsync(preparation).ConfigureAwait(false);
        }
        finally
        {
            preparation.ExitOperation();
            if (releaseDispatchGate)
            {
                _dispatchGate.Release();
            }
        }
    }

    private async ValueTask<bool> CompletePublicationCoreAsync(ExtensionGenerationPreparation preparation)
    {
        if (preparation.State == 2)
        {
            return true;
        }

        if (preparation.State != 1)
        {
            return false;
        }

        // PrepareGenerationAsync holds the dispatch gate from preparation through
        // this commit. ReadyToPublishAsync has already completed all fallible
        // candidate handoff work, so state 1 is the Host handoff commit point.
        // Do not revalidate or roll back here: Host may already be leasing the
        // generation, and manager ownership must follow that immutable snapshot.
        lock (_gate)
        {
            SynchronizeLegacyRegistrationsLocked(preparation.Generation);
            _publishedDispatchGeneration = preparation.Generation;
            foreach (var candidate in preparation.Candidates)
            {
                _dispatchCandidates.Remove(candidate);
            }

            preparation.TryTransition(1, 2);
            _activePreparation = null;

        }

        // Detached registrations are no longer part of the manager's coherent
        // generation. Their cleanup is deliberately best effort and cannot turn
        // a completed Host handoff into a failed publication.
        foreach (var detached in preparation.DetachedPrevious)
        {
            try
            {
                await detached.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await detached.ReleaseAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            detached.MarkStopped();
        }

        return true;
    }

    internal async ValueTask<bool> AbortPreparationAsync(ExtensionGenerationPreparation preparation)
    {
        if (preparation.State == 2 || preparation.State == 3)
        {
            return false;
        }

        await preparation.EnterOperationAsync().ConfigureAwait(false);
        var releaseDispatchGate = false;
        try
        {
            // DisposeAsync cancels the lifetime before asking an active
            // preparation to abort. A ready preparation has already handed
            // ownership to Host, so finalize it rather than restoring the old
            // manager maps underneath a possible new Host lease.
            if (preparation.State == 1 && _dispatchLifetime.IsCancellationRequested)
            {
                releaseDispatchGate = true;
                return await CompletePublicationCoreAsync(preparation).ConfigureAwait(false);
            }

            return await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
        }
        finally
        {
            preparation.ExitOperation();
            if (releaseDispatchGate)
            {
                _dispatchGate.Release();
            }
        }
    }

    private async ValueTask<bool> AbortPreparationCoreAsync(
        ExtensionGenerationPreparation preparation,
        bool releaseDispatchGate = true)
    {
        var state = preparation.State;
        if (state == 2 || state == 3 || !preparation.TryTransition(state, 3))
        {
            return false;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_activePreparation, preparation))
            {
                _activePreparation = null;
            }
        }

        if (state == 1)
        {
            foreach (var previous in preparation.ChangedPrevious)
            {
                previous.ResumeServing();
            }
        }

        try
        {
            var candidateSet = preparation.Candidates.ToHashSet();
            foreach (var candidate in preparation.Candidates)
            {
                try
                {
                    await AbortCandidateAsync(candidate).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            foreach (var context in preparation.Contexts)
            {
                if (!candidateSet.Contains(context.Instance))
                {
                    try
                    {
                        await context.ReleaseGenerationAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }

            return true;
        }
        finally
        {
            if (releaseDispatchGate)
            {
                _dispatchGate.Release();
            }
        }
    }


    private void SynchronizeLegacyRegistrationsLocked(ExtensionDispatchGeneration generation)
    {
        _instances.Clear();
        _handlers.Clear();
        _fallback = null;
        foreach (var context in generation.Contexts)
        {
            _instances[context.Instance.Manifest.Id] = context.Instance;
        }

        foreach (var pair in generation.HandlerBindings)
        {
            _handlers[pair.Key] = new HandlerBinding(pair.Value.Context.Instance, pair.Value.Handler, null);
        }

        if (generation.FallbackBinding is { } fallback)
        {
            _fallback = new HandlerBinding(fallback.Context.Instance, null, fallback.Fallback);
        }
    }

    private async ValueTask AbortUnpublishedAsync(
        IEnumerable<ExtensionDispatchContext> contexts,
        IEnumerable<ExtensionDispatchContext> candidateContexts,
        IEnumerable<ExtensionInstance> candidates)
    {
        foreach (var candidate in candidates.Distinct())
        {
            await AbortCandidateAsync(candidate).ConfigureAwait(false);
        }

        var candidateSet = candidateContexts.Select(static context => context.Instance).ToHashSet();
        foreach (var context in contexts.Distinct())
        {
            if (!candidateSet.Contains(context.Instance))
            {
                await context.ReleaseGenerationAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask AbortCandidateAsync(ExtensionInstance candidate)
    {
        lock (_gate)
        {
            _dispatchCandidates.Remove(candidate);
        }

        await candidate.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
    }

    private static ImmutableArray<string> NormalizeHandlerIds(ImmutableArray<string> ids) =>
        ids.IsDefault
            ? ImmutableArray<string>.Empty
            : ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToImmutableArray();

    private static bool HasExactIdentity(
        ExtensionDispatchContext context,
        ExtensionManifest manifest,
        ExtensionSettingsConfiguration? settings) =>
        string.Equals(context.Instance.Manifest.Id, manifest.Id, StringComparison.Ordinal) &&
        string.Equals(context.Instance.Manifest.Version.ToString(), manifest.Version.ToString(), StringComparison.Ordinal) &&
        SettingsEqual(context.Settings, settings);

    private static bool SettingsEqual(
        ExtensionSettingsConfiguration? left,
        ExtensionSettingsConfiguration? right) =>
        left is null
            ? right is null
            : right is not null &&
              string.Equals(left.ExtensionId, right.ExtensionId, StringComparison.Ordinal) &&
              left.SchemaVersion == right.SchemaVersion &&
              string.Equals(left.SettingsJson, right.SettingsJson, StringComparison.Ordinal) &&
              left.Version == right.Version;

    private static ExtensionFailureCode FindHandlerCollision(
        ImmutableArray<string> selectedIds,
        IReadOnlyDictionary<string, IExtensionHandler> actualHandlers,
        Dictionary<string, ExtensionDispatchBinding> existing)
    {
        foreach (var handlerId in selectedIds)
        {
            if (actualHandlers.ContainsKey(handlerId) && existing.ContainsKey(handlerId))
            {
                return ExtensionFailureCode.HandlerConflict;
            }
        }

        return ExtensionFailureCode.None;
    }

    private static ExtensionGenerationBindingStatus CreateUnavailableStatus(
        string? extensionId,
        string? version,
        ImmutableArray<string>? requested,
        ExtensionFailureCode failureCode) =>
        new(
            extensionId,
            version,
            false,
            false,
            failureCode,
            requested ?? ImmutableArray<string>.Empty,
            requested ?? ImmutableArray<string>.Empty,
            false);

    private static void AddChangedPrevious(List<ExtensionInstance> changed, ExtensionInstance instance)
    {
        if (!changed.Contains(instance))
        {
            changed.Add(instance);
        }
    }
}

internal sealed partial class ExtensionInstance
{
    internal ExtensionSettingsConfiguration? Settings { get; private set; }

    internal void SetSettings(ExtensionSettingsConfiguration? settings) => Settings = settings;

    internal ValueTask NotifyExternalFailureAsync(ExtensionFailureCode category, Exception exception)
    {
        _lastFailure = category;
        return NotifyFailureAsync(exception);
    }
}
