using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

public sealed partial class ExtensionRuntimeManager
{
    private async ValueTask<CandidateResult> StartCandidateAsync(
        ExtensionManifest manifest,
        ExtensionSettingsConfiguration? settings,
        bool reloading,
        CancellationToken cancellationToken,
        ImmutableArray<Guid> routeIds = default)
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
                _capabilityFactory,
                routeIds);
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
