using System.Collections.Immutable;
using System.Globalization;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

public sealed partial class ExtensionRuntimeManager
{
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly HashSet<ExtensionInstance> _dispatchCandidates = new();
    private ExtensionGenerationPreparation? _activePreparation;
    private ExtensionDispatchGeneration? _publishedDispatchGeneration;
    private long _nextDispatchGenerationId;


    internal async ValueTask<ExtensionGenerationCommitResult> ReadyToPublishAsync(
        ExtensionGenerationPreparation preparation,
        CancellationToken cancellationToken)
    {
        string[]? suspendedIds = null;
        var keepSuspended = false;
        await preparation.EnterOperationAsync().ConfigureAwait(false);
        try
        {
            if (preparation.State == 2)
            {
                return ExtensionGenerationCommitResult.Success(preparation.Generation, preparation.Previous);
            }

            if (preparation.State != 0 || !preparation.TryTransition(0, 1))
            {
                var currentState = preparation.State;
                if (currentState == 2)
                {
                    return ExtensionGenerationCommitResult.Success(preparation.Generation, preparation.Previous);
                }

                return ExtensionGenerationCommitResult.Failure(
                    ExtensionFailureCode.RuntimeUnavailable,
                    new ExtensionErrorDetail(
                        $"ReadyToPublishAsync could not start for extension generation '{preparation.Generation.GenerationId}' because preparation is in state '{currentState}'."),
                    preparation.Previous);
            }

            using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _dispatchLifetime.Token);
            var operationToken = readinessCancellation.Token;
            try
            {
                ExtensionDispatchGeneration? currentGeneration;
                bool generationIsStale;
                lock (_gate)
                {
                    currentGeneration = _publishedDispatchGeneration;
                    generationIsStale = _disposed ||
                        (preparation.Previous is null
                            ? currentGeneration is not null
                            : currentGeneration is not null &&
                              !ReferenceEquals(currentGeneration, preparation.Previous));
                }

                if (generationIsStale)
                {
                    await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                    var currentGenerationId = currentGeneration?.GenerationId.ToString(CultureInfo.InvariantCulture) ?? "none";
                    return ExtensionGenerationCommitResult.Failure(
                        ExtensionFailureCode.RuntimeUnavailable,
                        new ExtensionErrorDetail(
                            $"Prepared extension generation '{preparation.Generation.GenerationId}' is stale; current generation is '{currentGenerationId}'."),
                        preparation.Previous);
                }

                // Suspend dispatch for every replaced and removed extension before draining so
                // new requests wait for the replacement instead of failing against a draining
                // instance. CompletePublicationCoreAsync resumes them with the new instances.
                suspendedIds = preparation.ChangedPrevious
                    .Select(static previous => previous.Manifest.Id)
                    .Concat(preparation.DetachedPrevious.Select(static detached => detached.Manifest.Id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var suspendedId in suspendedIds)
                {
                    GetTurnstile(suspendedId).Suspend();
                }

                foreach (var previous in preparation.ChangedPrevious)
                {
                    operationToken.ThrowIfCancellationRequested();
                    previous.MarkDraining();
                    PublishExtensionState(previous, ExtensionLoadState.Unloading);
                }

                // The replaced instances stay loaded-but-draining through the Host handoff
                // window: their stop and the OnPreviousStoppedAsync candidate hook are
                // deferred to CompletePublicationCoreAsync so requests suspended on the
                // turnstile keep a live serving instance until the new generation is
                // published and its turnstiles are resumed.

                foreach (var candidate in preparation.Candidates)
                {
                    operationToken.ThrowIfCancellationRequested();
                    candidate.MarkServing();
                }

                // The suspension outlives this method on purpose: it spans the Host handoff
                // window and is lifted by CompletePublicationCoreAsync with the new instances.
                keepSuspended = true;
                return ExtensionGenerationCommitResult.Success(preparation.Generation, preparation.Previous);
            }
            catch (OperationCanceledException exception)
            {
                if (_logger is { } cancelledLogger)
                {
                    ExtensionLogMessages.ExtensionGenerationCommitCancelled(cancelledLogger, nameof(ReadyToPublishAsync));
                }

                await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                return ExtensionGenerationCommitResult.Failure(
                    ExtensionFailureCode.Cancelled,
                    new ExtensionErrorDetail(
                        $"ReadyToPublishAsync for extension generation '{preparation.Generation.GenerationId}' was cancelled before publication ({exception.GetType().Name})."),
                    preparation.Previous);
            }
            catch (Exception exception)
            {
                if (_logger is { } failedLogger)
                {
                    ExtensionLogMessages.ExtensionGenerationCommitFailed(
                        failedLogger,
                        exception,
                        nameof(ReadyToPublishAsync));
                }

                await AbortPreparationCoreAsync(preparation).ConfigureAwait(false);
                return ExtensionGenerationCommitResult.Failure(
                    ExtensionFailureCode.RuntimeUnavailable,
                    new ExtensionErrorDetail(
                        $"ReadyToPublishAsync failed for extension generation '{preparation.Generation.GenerationId}' ({exception.GetType().Name})."),
                    preparation.Previous);
            }
        }
        finally
        {
            if (!keepSuspended && suspendedIds is not null)
            {
                // Failure and cancellation exits resume waiters against the honestly-reported
                // current state (the abort path decides serving vs stopped per instance).
                ResumeTurnstilesToCurrent(suspendedIds);
            }

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

            // Lift the ReadyToPublishAsync suspension: waiters re-resolve against the
            // committed generation. Removed extensions resume to null (permanently
            // unavailable); unchanged members were never suspended, so resuming them only
            // refreshes the current-instance pointer.
            var generationIds = preparation.Generation.Contexts
                .Select(static context => context.Instance.Manifest.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var context in preparation.Generation.Contexts)
            {
                GetTurnstile(context.Instance.Manifest.Id).Resume(context.Instance);
            }

            foreach (var detached in preparation.DetachedPrevious)
            {
                if (!generationIds.Contains(detached.Manifest.Id))
                {
                    GetTurnstile(detached.Manifest.Id).Resume(null);
                }
            }
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
            catch (Exception exception)
            {
                if (_logger is { } logger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        logger,
                        exception,
                        detached.Manifest.Id,
                        nameof(ExtensionInstance.StopForReplacementAsync));
                }
            }

            try
            {
                await detached.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_logger is { } logger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        logger,
                        exception,
                        detached.Manifest.Id,
                        nameof(ExtensionInstance.ReleaseAsync));
                }
            }

            detached.MarkStopped();
        }
        foreach (var candidate in preparation.Candidates)
        {
            PublishExtensionState(candidate, ExtensionLoadState.Loaded);
        }

        var stoppedInstances = new HashSet<ExtensionInstance>();
        foreach (var previous in preparation.ChangedPrevious)
        {
            if (!stoppedInstances.Add(previous))
            {
                continue;
            }

            // ReadyToPublishAsync deliberately left the replaced generation loaded-but-draining
            // so requests suspended across the Host handoff stayed serviceable. The new
            // generation is now published and its turnstiles resumed, so retire the previous
            // generation. Stop and lifecycle-hook failures are best effort here: the handoff is
            // already committed, so an honest Stopped state wins over abort or resurrection.
            var stopFailed = false;
            try
            {
                stopFailed = !await previous.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailed = true;
                if (_logger is { } stopLogger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        stopLogger,
                        exception,
                        previous.Manifest.Id,
                        nameof(ExtensionInstance.StopForReplacementAsync));
                }
            }

            if (stopFailed && _logger is { } stopFailureLogger)
            {
                ExtensionLogMessages.ExtensionCandidateFailed(
                    stopFailureLogger,
                    previous.Manifest.Id,
                    ExtensionFailureCode.StopFailed.ToString());
            }

            // The hook only makes sense once the previous instance left the serving set, so it
            // follows the deferred stop instead of the pre-handoff drain phase.
            foreach (var candidate in preparation.Candidates)
            {
                if (!string.Equals(candidate.Manifest.Id, previous.Manifest.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var hookFailed = false;
                try
                {
                    hookFailed = !await candidate.NotifyPreviousStoppedAsync(LifecycleTimeout)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    hookFailed = true;
                    if (_logger is { } hookLogger)
                    {
                        ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                            hookLogger,
                            exception,
                            candidate.Manifest.Id,
                            nameof(ExtensionInstance.NotifyPreviousStoppedAsync));
                    }
                }

                if (hookFailed && _logger is { } hookFailureLogger)
                {
                    ExtensionLogMessages.ExtensionCandidateFailed(
                        hookFailureLogger,
                        candidate.Manifest.Id,
                        ExtensionFailureCode.LifecycleFailed.ToString());
                }
            }

            previous.MarkStopped();
            PublishExtensionState(previous, ExtensionLoadState.Stopped);
        }

        foreach (var detached in preparation.DetachedPrevious)
        {
            if (stoppedInstances.Add(detached))
            {
                PublishExtensionState(detached, ExtensionLoadState.Stopped);
            }
        }

        return true;
    }

    /// <summary>Best-effort stops the replaced previous instances of the stored active preparation after a post-swap publication divergence.</summary>
    /// <param name="cancellationToken">The caller cancellation token; cancellation before the first stop skips the abandoned cleanup.</param>
    /// <remarks>
    /// The Host snapshot may already reference the prepared generation while the manager never
    /// adopted it (publication completion returned false after a successful replacement). Stopping
    /// the replaced previous instances keeps the diverged old generation from serving alongside
    /// the live publication; every failure stays best effort because the publication outcome is
    /// already decided and the caller only needs the double-run closed.
    /// </remarks>
    internal async Task AbandonReplacedPreviousAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ExtensionGenerationPreparation? preparation;
        lock (_gate)
        {
            preparation = _activePreparation;
        }

        if (preparation is null)
        {
            return;
        }

        foreach (var previous in preparation.ChangedPrevious)
        {
            if (previous.GetStatus().State == ExtensionLoadState.Stopped)
            {
                continue;
            }

            try
            {
                await previous.StopForReplacementAsync(LifecycleTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_logger is { } logger)
                {
                    ExtensionLogMessages.ExtensionInstanceReleaseFailed(
                        logger,
                        exception,
                        previous.Manifest.Id,
                        nameof(ExtensionInstance.StopForReplacementAsync));
                }
            }

            previous.MarkStopped();
            PublishExtensionState(previous, ExtensionLoadState.Stopped);
        }
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
                if (previous.StopStarted)
                {
                    // The generation went through the one-way stop pipeline
                    // (drain, StopAsync, task/event teardown); flipping its
                    // state back would resurrect a zombie that admits requests
                    // it cannot serve. Report the stop honestly; the publisher
                    // schedules a recovery publication to start a fresh
                    // candidate.
                    previous.MarkStopped();
                    PublishExtensionState(previous, ExtensionLoadState.Stopped);
                }
                else
                {
                    previous.ResumeServing();
                    PublishExtensionState(previous, ExtensionLoadState.Loaded);
                }
            }
        }

        // Release any turnstiles suspended by ReadyToPublishAsync; waiters observe the
        // honestly-reported state (resumed old instance, stopped old instance, or none).
        ResumeTurnstilesToCurrent(preparation.ChangedPrevious
            .Select(static previous => previous.Manifest.Id)
            .Concat(preparation.DetachedPrevious.Select(static detached => detached.Manifest.Id))
            .Distinct(StringComparer.Ordinal));

        try
        {
            if (_logger is { } abortLogger)
            {
                ExtensionLogMessages.ExtensionGenerationPreparationAborted(
                    abortLogger,
                    preparation.Generation.GenerationId,
                    preparation.Candidates.Length);
            }

            var candidateSet = preparation.Candidates.ToHashSet();
            foreach (var candidate in preparation.Candidates)
            {
                try
                {
                    await AbortCandidateAsync(candidate).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (_logger is { } logger)
                    {
                        ExtensionLogMessages.ExtensionGenerationCandidateAbortFailed(
                            logger,
                            exception,
                            candidate.Manifest.Id,
                            nameof(AbortPreparationCoreAsync));
                    }
                }
            }

            foreach (var context in preparation.Contexts)
            {
                if (!candidateSet.Contains(context.Instance))
                {
                    try
                    {
                        await context.ReleaseGenerationAsync(preparation.Generation.GenerationId).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if (_logger is { } logger)
                        {
                            ExtensionLogMessages.ExtensionGenerationContextReleaseFailed(
                                logger,
                                exception,
                                context.Instance.Manifest.Id,
                                nameof(AbortPreparationCoreAsync));
                        }
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


    /// <summary>Enters dispatch for one generation-bound instance through its extension's turnstile.</summary>
    /// <param name="instance">The generation-bound instance to enter.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The entered instance, or null when unavailable or replaced.</returns>
    private ValueTask<ExtensionInstance?> EnterGenerationDispatchAsync(
        ExtensionInstance instance,
        CancellationToken cancellationToken) =>
        GetTurnstile(instance.Manifest.Id).EnterAsync(instance, cancellationToken);

    /// <summary>Gets whether the instance's extension turnstile is suspended for a pending publication.</summary>
    /// <param name="instance">The generation-bound instance to inspect.</param>
    /// <returns><see langword="true" /> while entry waits for the publication to resolve.</returns>
    private bool IsGenerationEntrySuspended(ExtensionInstance instance) =>
        GetTurnstile(instance.Manifest.Id).IsSuspended;

    /// <summary>Waits for the instance's extension turnstile suspension to resolve within the entry budget.</summary>
    /// <param name="instance">The generation-bound instance to wait for.</param>
    /// <param name="timeout">The remaining shared entry budget.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns><see langword="true" /> when entry is open; <see langword="false" /> when the budget elapsed first.</returns>
    private ValueTask<bool> WaitForGenerationEntryResolutionAsync(
        ExtensionInstance instance,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        GetTurnstile(instance.Manifest.Id).WaitForSuspensionResolutionAsync(timeout, cancellationToken);

    /// <summary>Gets the currently published dispatch generation for rebind retries.</summary>
    /// <returns>The published generation, or null when none is active.</returns>
    private ExtensionDispatchGeneration? GetPublishedGeneration()
    {
        lock (_gate)
        {
            return _publishedDispatchGeneration;
        }
    }

    /// <summary>Resumes each extension's turnstile to the instance currently registered for it (or null when none is).</summary>
    /// <param name="extensionIds">The extension identifiers to resume.</param>
    private void ResumeTurnstilesToCurrent(IEnumerable<string> extensionIds)
    {
        foreach (var extensionId in extensionIds)
        {
            ExtensionInstance? current;
            lock (_gate)
            {
                _instances.TryGetValue(extensionId, out current);
            }

            // A stopped instance can never serve again; resuming waiters onto it would hand
            // them a zombie. Only an instance that still serves resolves as the resume target.
            var resumable = current is not null &&
                !current.StopStarted &&
                current.GetStatus().State == ExtensionLoadState.Loaded
                ? current
                : null;
            GetTurnstile(extensionId).Resume(resumable);
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
            _handlers[pair.Key] = new HandlerBinding(
                pair.Value.Context.Instance,
                pair.Value.Handler,
                pair.Value.StreamingHandler,
                null);
        }

        if (generation.FallbackBinding is { } fallback)
        {
            _fallback = new HandlerBinding(fallback.Context.Instance, null, null, fallback.Fallback);
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
        ExtensionSettingsConfiguration? settings,
        ImmutableArray<Guid> routeIds,
        string? contentHash) =>
        string.Equals(context.Instance.Manifest.Id, manifest.Id, StringComparison.Ordinal) &&
        string.Equals(context.Instance.Manifest.Version.ToString(), manifest.Version.ToString(), StringComparison.Ordinal) &&
        SettingsEqual(context.Settings, settings) &&
        string.Equals(context.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase) &&
        context.RouteRegistrations is not null &&
        context.RouteRegistrations.HasSameOwnedRoutes(routeIds);

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
        IReadOnlyDictionary<string, IExtensionStreamingHandler> actualStreamingHandlers,
        Dictionary<string, ExtensionDispatchBinding> existing)
    {
        foreach (var handlerId in selectedIds)
        {
            if ((actualHandlers.ContainsKey(handlerId) || actualStreamingHandlers.ContainsKey(handlerId)) &&
                existing.ContainsKey(handlerId))
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
        ExtensionFailureCode failureCode,
        ExtensionErrorDetail failureDetail) =>
        new(
            extensionId,
            version,
            false,
            false,
            failureCode,
            requested ?? ImmutableArray<string>.Empty,
            requested ?? ImmutableArray<string>.Empty,
            false,
            failureDetail);

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
