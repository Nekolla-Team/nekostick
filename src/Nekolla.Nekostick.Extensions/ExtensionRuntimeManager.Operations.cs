using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

public sealed partial class ExtensionRuntimeManager
{
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
            if (_logger is { } unloadedLogger)
            {
                var version = instance.Manifest.Version.ToString();
                ExtensionLogMessages.ExtensionUnloaded(
                    unloadedLogger,
                    instance.Manifest.Id,
                    version);
            }

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

        using var callbackScope = ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route);

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

    /// <summary>Dispatches one streaming handler by stable ID without exposing runtime handles.</summary>
    /// <param name="handlerId">The stable handler ID.</param>
    /// <param name="request">The streaming request value.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A safe streaming invocation result.</returns>
    public async ValueTask<ExtensionStreamingInvocationResult> HandleStreamingAsync(
        string? handlerId,
        ExtensionStreamingRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerId) || request is null)
        {
            return ExtensionStreamingInvocationResult.Unavailable;
        }

        HandlerBinding? binding;
        lock (_gate)
        {
            if (_activePreparation is not null || _publishedDispatchGeneration is not null)
            {
                return ExtensionStreamingInvocationResult.Unavailable;
            }

            _handlers.TryGetValue(handlerId, out binding);
        }

        if (binding is null || binding.StreamingHandler is not { } handler ||
            !binding.Instance.IsStreamingHandler(handlerId) ||
            !binding.Instance.TryEnterRequest())
        {
            request.BodyStream.Dispose();
            return ExtensionStreamingInvocationResult.Unavailable;
        }

        var holdRequestLease = false;
        try
        {
            ExtensionStreamingResponse? response;
            using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route))
            {
                try
                {
                    response = await handler.HandleStreamingAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ExtensionRequestBodyLimitExceededException)
                {
                    return ExtensionStreamingInvocationResult.Failed;
                }
                catch (ExtensionRequestReadTimeoutException)
                {
                    return ExtensionStreamingInvocationResult.Failed;
                }
                catch (OperationCanceledException)
                {
                    return ExtensionStreamingInvocationResult.Failed;
                }
                catch (Exception exception)
                {
                    await RecordFailureAsync(binding.Instance, ExtensionFailureCode.HandlerFailed, exception)
                        .ConfigureAwait(false);
                    return ExtensionStreamingInvocationResult.Failed;
                }
            }

            if (response is null)
            {
                return ExtensionStreamingInvocationResult.Failed;
            }

            holdRequestLease = true;
            return ExtensionStreamingInvocationResult.Handled(response, binding.Instance);
        }
        finally
        {
            try
            {
                request.BodyStream.Dispose();
            }
            catch
            {
            }

            if (!holdRequestLease)
            {
                binding.Instance.LeaveRequest();
            }
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
}
