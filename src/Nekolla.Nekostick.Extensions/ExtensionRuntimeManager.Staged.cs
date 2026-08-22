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
                    PublishExtensionState(previous, ExtensionLoadState.Unloading);
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
        foreach (var candidate in preparation.Candidates)
        {
            PublishExtensionState(candidate, ExtensionLoadState.Loaded);
        }

        var stoppedInstances = new HashSet<ExtensionInstance>();
        foreach (var previous in preparation.ChangedPrevious)
        {
            if (stoppedInstances.Add(previous))
            {
                previous.MarkStopped();
                PublishExtensionState(previous, ExtensionLoadState.Stopped);
            }
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
                PublishExtensionState(previous, ExtensionLoadState.Loaded);
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
        ExtensionSettingsConfiguration? settings,
        ImmutableArray<Guid> routeIds) =>
        string.Equals(context.Instance.Manifest.Id, manifest.Id, StringComparison.Ordinal) &&
        string.Equals(context.Instance.Manifest.Version.ToString(), manifest.Version.ToString(), StringComparison.Ordinal) &&
        SettingsEqual(context.Settings, settings) &&
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
