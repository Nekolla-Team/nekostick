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
        ImmutableArray<Guid> routeIds = default,
        string? contentHash = null,
        IReadOnlyDictionary<string, SemVersion>? availableDependencyVersions = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CandidateResult.Failure(
                ExtensionFailureCode.Cancelled,
                new ExtensionErrorDetail($"Startup for extension '{manifest.Id}' was canceled before it began."));
        }

        var contractFailure = ValidateManifestContracts(manifest, out var contractFailureDetail);
        if (contractFailure != ExtensionFailureCode.None)
        {
            return CandidateResult.Failure(contractFailure, contractFailureDetail!);
        }

        availableDependencyVersions ??= GetLiveDependencyVersions(manifest);
        var loaded = _loader.Load(manifest, contentHash);
        if (!loaded.Succeeded || loaded.Handle is null)
        {
            var loadFailureCode = loaded.FailureCode.ToString();
            var loadFailureDetail = loaded.FailureDetail!;
            if (_logger is { } loadLogger)
            {
                if (loaded.Exception is not null)
                {
                    ExtensionLogMessages.ExtensionFailureDetails(
                        loadLogger,
                        loaded.Exception,
                        manifest.Id,
                        loadFailureCode);
                }

                ExtensionLogMessages.ExtensionCandidateFailed(loadLogger, manifest.Id, loadFailureCode);
            }

            return CandidateResult.Failure(loaded.FailureCode, loadFailureDetail);
        }

        var loadedHandle = loaded.Handle;
        ExtensionInstance? instance = null;
        try
        {
            instance = new ExtensionInstance(
                manifest,
                loadedHandle,
                _hostApiVersion,
                settings,
                // `instance` is assigned immediately after construction and always before
                // `StartAsync`; contract imports only run inside the startup window, so the
                // closure never observes the null state.
                (contractId, contractType, requiredRange) =>
                    ResolveContractProvider(instance!, contractId, contractType, requiredRange),
                availableDependencyVersions,
                _capabilityFactory,
                routeIds,
                _dataDirectory,
                _logger);
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
                // Preserve the instance's classified failure (Cancelled, HandlerConflict,
                // RegistrationRejected-derived codes) instead of collapsing everything to
                // LifecycleFailed; the publisher's unsafe-binding policy keys off these codes.
                var failureStatus = instance.GetStatus();
                var failureCode = failureStatus.LastFailure;
                if (failureCode == ExtensionFailureCode.None)
                {
                    failureCode = ExtensionFailureCode.LifecycleFailed;
                }

                var failureDetail = failureStatus.LastFailureDetail ?? new ExtensionErrorDetail(
                    $"Extension '{manifest.Id}' did not complete lifecycle startup ({failureCode}).");
                await instance.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
                if (_logger is { } lifecycleLogger)
                {
                    ExtensionLogMessages.ExtensionCandidateFailed(lifecycleLogger, manifest.Id, failureCode.ToString());
                }

                return CandidateResult.Failure(failureCode, failureDetail);
            }

            return CandidateResult.Success(instance);
        }
        catch (Exception exception)
        {
            if (instance is not null)
            {
                await instance.AbortAsync(LifecycleTimeout).ConfigureAwait(false);
            }
            else
            {
                loaded.Handle.Unload();
            }

            if (_logger is { } constructorLogger)
            {
                var failureCode = ExtensionFailureCode.EntryConstructorFailed.ToString();
                ExtensionLogMessages.ExtensionFailureDetails(
                    constructorLogger,
                    exception,
                    manifest.Id,
                    failureCode);
                ExtensionLogMessages.ExtensionCandidateFailed(
                    constructorLogger,
                    manifest.Id,
                    failureCode);
            }

            return CandidateResult.Failure(
                ExtensionFailureCode.EntryConstructorFailed,
                ExtensionErrorDetail.FromException(exception));
        }
    }
    private async ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(
        ExtensionInstance instance,
        CancellationToken cancellationToken)
    {
        if (ExtensionCallbackGuard.IsActive)
        {
            return new(
                false,
                ExtensionLifecycleOperationCode.Reentrant,
                instance.GetLifecycleStatus(),
                new ExtensionErrorDetail($"Extension '{instance.Manifest.Id}' cannot request a reload from an active extension callback."));
        }

        try
        {
            var result = await ReloadAsync(instance.Manifest, instance.Settings, cancellationToken).ConfigureAwait(false);
            return ToLifecycleResult(result);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (_logger is { } cancelledLogger)
            {
                ExtensionLogMessages.ExtensionLifecycleOperationCancelled(
                    cancelledLogger,
                    instance.Manifest.Id,
                    nameof(RequestReloadAsync));
            }

            return new(
                false,
                ExtensionLifecycleOperationCode.Cancelled,
                instance.GetLifecycleStatus(),
                ExtensionErrorDetail.FromException(exception));
        }
        catch (Exception exception)
        {
            if (_logger is { } failedLogger)
            {
                ExtensionLogMessages.ExtensionLifecycleOperationFailed(
                    failedLogger,
                    exception,
                    instance.Manifest.Id,
                    nameof(RequestReloadAsync));
            }

            return new(
                false,
                ExtensionLifecycleOperationCode.Failed,
                instance.GetLifecycleStatus(),
                ExtensionErrorDetail.FromException(exception));
        }
    }
    private async ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(
        ExtensionInstance instance,
        CancellationToken cancellationToken)
    {
        if (ExtensionCallbackGuard.IsActive)
        {
            return new(
                false,
                ExtensionLifecycleOperationCode.Reentrant,
                instance.GetLifecycleStatus(),
                new ExtensionErrorDetail($"Extension '{instance.Manifest.Id}' cannot request unload from an active extension callback."));
        }

        try
        {
            var result = await UnloadAsync(instance.Manifest.Id, cancellationToken).ConfigureAwait(false);
            return ToLifecycleResult(result);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (_logger is { } cancelledLogger)
            {
                ExtensionLogMessages.ExtensionLifecycleOperationCancelled(
                    cancelledLogger,
                    instance.Manifest.Id,
                    nameof(RequestUnloadAsync));
            }

            return new(
                false,
                ExtensionLifecycleOperationCode.Cancelled,
                instance.GetLifecycleStatus(),
                ExtensionErrorDetail.FromException(exception));
        }
        catch (Exception exception)
        {
            if (_logger is { } failedLogger)
            {
                ExtensionLogMessages.ExtensionLifecycleOperationFailed(
                    failedLogger,
                    exception,
                    instance.Manifest.Id,
                    nameof(RequestUnloadAsync));
            }

            return new(
                false,
                ExtensionLifecycleOperationCode.Failed,
                instance.GetLifecycleStatus(),
                ExtensionErrorDetail.FromException(exception));
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
            result.Status is null ? null : ToLifecycleStatus(result.Status),
            result.FailureDetail);

    internal static ExtensionLifecycleStatus ToLifecycleStatus(ExtensionRuntimeStatus status) =>
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
                ExtensionFailureCode.None => ExtensionLifecycleFailureCode.None,
                ExtensionFailureCode.InvalidArgument => ExtensionLifecycleFailureCode.InvalidArgument,
                ExtensionFailureCode.Cancelled => ExtensionLifecycleFailureCode.Cancelled,
                ExtensionFailureCode.AlreadyStopped or ExtensionFailureCode.AlreadyUnloaded => ExtensionLifecycleFailureCode.AlreadyStopped,
                ExtensionFailureCode.ExtensionNotLoaded => ExtensionLifecycleFailureCode.ExtensionNotLoaded,
                ExtensionFailureCode.LoadFailed or ExtensionFailureCode.EntryTypeMissing or
                    ExtensionFailureCode.EntryTypeNotCompatible or ExtensionFailureCode.EntryConstructorFailed => ExtensionLifecycleFailureCode.LoadFailed,
                ExtensionFailureCode.LifecycleFailed => ExtensionLifecycleFailureCode.LifecycleFailed,
                ExtensionFailureCode.StopFailed or ExtensionFailureCode.DrainTimeout => ExtensionLifecycleFailureCode.StopFailed,
                ExtensionFailureCode.HandlerFailed => ExtensionLifecycleFailureCode.HandlerFailed,
                ExtensionFailureCode.CallbackFailed or ExtensionFailureCode.FailureThresholdReached => ExtensionLifecycleFailureCode.CallbackFailed,
                ExtensionFailureCode.HandlerConflict or ExtensionFailureCode.FallbackConflict => ExtensionLifecycleFailureCode.RegistrationConflict,
                ExtensionFailureCode.ContractCatalogUnavailable or ExtensionFailureCode.DuplicateContractDeclaration or
                    ExtensionFailureCode.MissingContractProvider or ExtensionFailureCode.ContractVersionIncompatible or
                    ExtensionFailureCode.ContractIdentityMismatch or ExtensionFailureCode.ContractsIdentityMismatch => ExtensionLifecycleFailureCode.ContractConflict,
                ExtensionFailureCode.ReplacementPreserved => ExtensionLifecycleFailureCode.ReplacementPreserved,
                ExtensionFailureCode.UnloadLeak or ExtensionFailureCode.UnloadNotConfirmed => ExtensionLifecycleFailureCode.AlcUnloadUnconfirmed,
                ExtensionFailureCode.ManifestMissing or ExtensionFailureCode.DuplicateManifest or
                    ExtensionFailureCode.YamlInvalid or ExtensionFailureCode.JsonInvalid or
                    ExtensionFailureCode.UnknownManifestField or ExtensionFailureCode.DuplicateManifestField or
                    ExtensionFailureCode.ManifestSchemaInvalid or ExtensionFailureCode.InvalidIdentifier or
                    ExtensionFailureCode.InvalidVersion or ExtensionFailureCode.InvalidVersionRange or
                    ExtensionFailureCode.UnsafePath or ExtensionFailureCode.EntryAssemblyMissing or
                    ExtensionFailureCode.HostApiIncompatible or ExtensionFailureCode.DuplicateExtensionId or
                    ExtensionFailureCode.MissingDependency or ExtensionFailureCode.DependencyVersionIncompatible or
                    ExtensionFailureCode.DependencyCycle => ExtensionLifecycleFailureCode.ManifestInvalid,
                _ => ExtensionLifecycleFailureCode.RuntimeUnavailable
            },
            status.LastFailureDetail);


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
    private ExtensionFailureCode ValidateManifestContracts(
        ExtensionManifest manifest,
        out ExtensionErrorDetail? failureDetail)
    {
        failureDetail = null;
        var imports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in manifest.Imports)
        {
            if (!imports.Add(import.ContractId))
            {
                failureDetail = new ExtensionErrorDetail(
                    $"Extension '{manifest.Id}' declares contract import '{import.ContractId}' more than once.");
                return ExtensionFailureCode.DuplicateContractDeclaration;
            }

            var provider = manifest.Exports.FirstOrDefault(export =>
                string.Equals(export.ContractId, import.ContractId, StringComparison.Ordinal));
            if (provider is null && !TryFindContractProvider(import, out provider))
            {
                if (import.Optional)
                {
                    continue;
                }

                failureDetail = new ExtensionErrorDetail(
                    $"Required contract import '{import.ContractId}' has no available provider.");
                return ExtensionFailureCode.MissingContractProvider;
            }

            if (!import.VersionRange.IsSatisfiedBy(provider.Version))
            {
                if (import.Optional)
                {
                    continue;
                }

                failureDetail = new ExtensionErrorDetail(
                    $"Provider '{provider.ContractId}' version '{provider.Version}' does not satisfy import range '{import.VersionRange}' for extension '{manifest.Id}'.");
                return ExtensionFailureCode.ContractVersionIncompatible;
            }

            if (!string.Equals(import.AssemblyIdentity, provider.AssemblyIdentity, StringComparison.Ordinal) ||
                !string.Equals(import.TypeIdentity, provider.TypeIdentity, StringComparison.Ordinal))
            {
                failureDetail = new ExtensionErrorDetail(
                    $"Contract import '{import.ContractId}' declares assembly '{import.AssemblyIdentity}' and type '{import.TypeIdentity}', but its provider declares assembly '{provider.AssemblyIdentity}' and type '{provider.TypeIdentity}'.");
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
            // Candidates first: during generation preparation the incoming same-generation
            // provider MUST win over the live instance it is about to replace.
            foreach (var instance in _dispatchCandidates.Concat(_instances.Values))
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

    private Dictionary<string, SemVersion> GetLiveDependencyVersions(ExtensionManifest candidate)
    {
        lock (_gate)
        {
            var versions = new Dictionary<string, SemVersion>(StringComparer.Ordinal);
            foreach (var instance in _instances.Values.Concat(_dispatchCandidates))
            {
                versions[instance.Manifest.Id] = instance.Manifest.Version;
            }

            versions[candidate.Id] = candidate.Version;
            return versions;
        }
    }

    private readonly record struct ContractTargetResolution(
        ExtensionInstance? Provider,
        object? Target,
        ExtensionErrorDetail? Reason)
    {
        internal bool Succeeded => Provider is not null && Target is not null;
    }

    private ExtensionContractProviderResolution ResolveContractProvider(
        ExtensionInstance consumer,
        string contractId,
        Type contractType,
        SemVersionRange requiredRange)
    {
        var resolved = ResolveContractTarget(consumer.Manifest.Id, contractId, contractType, requiredRange);
        if (!resolved.Succeeded)
        {
            return ExtensionContractProviderResolution.Failure(resolved.Reason!);
        }

        var provider = resolved.Provider!;
        var target = resolved.Target!;
        object proxy;
        try
        {
            // Wrap outside _gate: first-use DispatchProxy codegen must not stall manager operations.
            proxy = provider.GetOrCreateContractProxy(
                contractId,
                consumer.Manifest.Id,
                contractType,
                target,
                GetTurnstile(provider.Manifest.Id),
                () =>
                {
                    var next = ResolveContractTarget(consumer.Manifest.Id, contractId, contractType, requiredRange);
                    return next.Succeeded
                        ? (next.Provider!, next.Target!, GetTurnstile(next.Provider!.Manifest.Id))
                        : null;
                });
        }
        catch (Exception exception)
        {
            return ExtensionContractProviderResolution.Failure(
                new ExtensionErrorDetail(
                    $"The proxy for contract '{contractId}' from provider extension '{provider.Manifest.Id}' is unavailable for expected type {ExtensionDiagnosticText.Value(contractType.FullName)} in required range {ExtensionDiagnosticText.Value(requiredRange.ToString())} ({exception.GetType().Name})."));
        }

        if (!contractType.IsInstanceOfType(proxy))
        {
            return ExtensionContractProviderResolution.Failure(
                new ExtensionErrorDetail(
                    $"The proxy for contract '{contractId}' from provider extension '{provider.Manifest.Id}' has runtime type {ExtensionDiagnosticText.Value(proxy.GetType().FullName)}, not expected contract type {ExtensionDiagnosticText.Value(contractType.FullName)}, in required range {ExtensionDiagnosticText.Value(requiredRange.ToString())}."));
        }

        return ExtensionContractProviderResolution.Success(proxy);
    }

    /// <summary>Finds the current provider instance and contract object for one import and records the consumer on it.</summary>
    /// <param name="consumerId">The importing extension identifier recorded on the provider.</param>
    /// <param name="contractId">The contract identifier to resolve.</param>
    /// <param name="contractType">The shared contract interface type.</param>
    /// <param name="requiredRange">The declared provider version range.</param>
    /// <returns>The provider and contract object, or a concrete provider-resolution failure.</returns>
    private ContractTargetResolution ResolveContractTarget(
        string consumerId,
        string contractId,
        Type contractType,
        SemVersionRange requiredRange)
    {
        ExtensionInstance? provider = null;
        object? target = null;
        var sawExport = false;
        var sawInRangeExport = false;
        (ExtensionInstance Provider, ExtensionContractExport Export)? versionMismatch = null;
        (ExtensionInstance Provider, ExtensionContractExport Export)? notYetExported = null;
        (ExtensionInstance Provider, ExtensionContractExport Export, Type ActualType)? typeMismatch = null;
        (ExtensionInstance Provider, ExtensionContractExport Export)? unavailable = null;
        lock (_gate)
        {
            // Candidates first: a consumer candidate starting during generation preparation must
            // bind the incoming provider, never the live instance being replaced (which would
            // leave a stale binding and record the consumer on the dying instance's table). The
            // published generation follows so proxies rebind across generation swaps, where the
            // serving instances never appear in the legacy _instances table.
            var published = _publishedDispatchGeneration;
            foreach (var instance in _dispatchCandidates
                .Concat(published is null
                    ? Enumerable.Empty<ExtensionInstance>()
                    : published.Contexts.Select(static context => context.Instance))
                .Concat(_instances.Values)
                .Distinct())
            {
                var export = instance.Manifest.Exports.FirstOrDefault(export =>
                    string.Equals(export.ContractId, contractId, StringComparison.Ordinal));
                if (export is null)
                {
                    continue;
                }

                sawExport = true;
                if (!requiredRange.IsSatisfiedBy(export.Version))
                {
                    versionMismatch ??= (instance, export);
                    continue;
                }

                sawInRangeExport = true;
                var resolution = instance.TryResolveContract(contractId, contractType, out var value);
                if (resolution == ExtensionContractExportResolution.Resolved && value is not null)
                {
                    provider = instance;
                    target = value;
                    break;
                }

                if (resolution == ExtensionContractExportResolution.TypeMismatch && value is not null)
                {
                    typeMismatch ??= (instance, export, value.GetType());
                }
                else if (resolution == ExtensionContractExportResolution.RegistryUnavailable)
                {
                    unavailable ??= (instance, export);
                }
                else
                {
                    notYetExported ??= (instance, export);
                }
            }
        }

        if (provider is not null && target is not null)
        {
            provider.TrackContractConsumer(consumerId);
            return new ContractTargetResolution(provider, target, null);
        }

        ExtensionErrorDetail reason;
        if (!sawExport)
        {
            reason = new ExtensionErrorDetail(
                $"No provider extension exports contract '{contractId}' requested by consumer extension '{consumerId}'.");
        }
        else if (!sawInRangeExport && versionMismatch is { } mismatch)
        {
            reason = new ExtensionErrorDetail(
                $"Provider extension '{mismatch.Provider.Manifest.Id}' exports contract '{contractId}' at version {ExtensionDiagnosticText.Value(mismatch.Export.Version.ToString())}, outside required range {ExtensionDiagnosticText.Value(requiredRange.ToString())} for consumer extension '{consumerId}'.");
        }
        else if (typeMismatch is { } wrongType)
        {
            reason = new ExtensionErrorDetail(
                $"Provider extension '{wrongType.Provider.Manifest.Id}' exports contract '{contractId}' at version {ExtensionDiagnosticText.Value(wrongType.Export.Version.ToString())} with runtime type {ExtensionDiagnosticText.Value(wrongType.ActualType.FullName)}, not expected type {ExtensionDiagnosticText.Value(contractType.FullName)} in required range {ExtensionDiagnosticText.Value(requiredRange.ToString())}.");
        }
        else if (notYetExported is { } pending)
        {
            reason = new ExtensionErrorDetail(
                $"Provider extension '{pending.Provider.Manifest.Id}' declares contract '{contractId}' at version {ExtensionDiagnosticText.Value(pending.Export.Version.ToString())} within required range {ExtensionDiagnosticText.Value(requiredRange.ToString())} but has not yet exported its implementation for consumer extension '{consumerId}'.");
        }
        else if (unavailable is { } unavailableProvider)
        {
            reason = new ExtensionErrorDetail(
                $"Provider extension '{unavailableProvider.Provider.Manifest.Id}' declares contract '{contractId}' at version {ExtensionDiagnosticText.Value(unavailableProvider.Export.Version.ToString())} within required range {ExtensionDiagnosticText.Value(requiredRange.ToString())}, but its contract registry is unavailable.");
        }
        else if (versionMismatch is { } outOfRange)
        {
            reason = new ExtensionErrorDetail(
                $"Provider extension '{outOfRange.Provider.Manifest.Id}' exports contract '{contractId}' at version {ExtensionDiagnosticText.Value(outOfRange.Export.Version.ToString())}, outside required range {ExtensionDiagnosticText.Value(requiredRange.ToString())} for consumer extension '{consumerId}'.");
        }
        else
        {
            reason = new ExtensionErrorDetail(
                $"No available provider for contract '{contractId}' satisfies required range {ExtensionDiagnosticText.Value(requiredRange.ToString())} for consumer extension '{consumerId}'.");
        }

        return new ContractTargetResolution(null, null, reason);
    }
    /// <summary>Computes the transitive closure of extensions that imported contracts from the given extension, based on the per-run records of the currently live instances.</summary>
    /// <param name="extensionId">The provider extension identifier whose dependents are requested.</param>
    /// <returns>The dependent extension identifiers, excluding the provider itself.</returns>
    internal ImmutableHashSet<string> GetCascadeReloadSet(string extensionId)
    {
        var result = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var pending = new Queue<string>();
            pending.Enqueue(extensionId);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!_instances.TryGetValue(current, out var instance))
                {
                    continue;
                }

                foreach (var consumerId in instance.SnapshotContractConsumers())
                {
                    if (!string.Equals(consumerId, extensionId, StringComparison.Ordinal) &&
                        result.Add(consumerId))
                    {
                        pending.Enqueue(consumerId);
                    }
                }
            }
        }

        return result.ToImmutable();
    }

    /// <summary>Publishes one required node-local core event without blocking the caller.</summary>
    /// <param name="event">The immutable core event.</param>
    /// <returns>The number of active extension queues that accepted the event.</returns>
    public int PublishCoreEvent(ExtensionCoreEvent? @event) =>
        PublishCoreEvent(@event, targetExtensionId: null);

    /// <summary>Publishes one core event to serving instances owned by one extension.</summary>
    /// <param name="event">The immutable core event.</param>
    /// <param name="targetExtensionId">The stable extension owner, or null to broadcast.</param>
    /// <returns>The number of active extension queues that accepted the event.</returns>
    public int PublishCoreEvent(
        ExtensionCoreEvent? @event,
        string? targetExtensionId)
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
                .Where(instance =>
                    instance.IsServing &&
                    (targetExtensionId is null ||
                        string.Equals(instance.Manifest.Id, targetExtensionId, StringComparison.Ordinal)))
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

    /// <summary>Gets or creates the dispatch turnstile for one extension identifier.</summary>
    /// <param name="extensionId">The extension identifier.</param>
    /// <returns>The shared turnstile surviving instance replacement.</returns>
    private ExtensionDispatchTurnstile GetTurnstile(string extensionId)
    {
        // _gate is re-entrant; callers already holding it are unaffected.
        lock (_gate)
        {
            if (!_turnstiles.TryGetValue(extensionId, out var turnstile))
            {
                turnstile = new ExtensionDispatchTurnstile();
                _turnstiles[extensionId] = turnstile;
            }

            return turnstile;
        }
    }

    private void CommitInstance(ExtensionInstance instance)
    {
        _instances[instance.Manifest.Id] = instance;
        GetTurnstile(instance.Manifest.Id).SetCurrent(instance);
        foreach (var pair in instance.Handlers)
        {
            _handlers[pair.Key] = new HandlerBinding(instance, pair.Value, null, null);
        }

        foreach (var pair in instance.StreamingHandlers)
        {
            _handlers[pair.Key] = new HandlerBinding(instance, null, pair.Value, null);
        }

        if (instance.Fallback is not null)
        {
            _fallback = new HandlerBinding(instance, null, null, instance.Fallback);
        }

        instance.MarkServing();
        PublishExtensionState(instance, ExtensionLoadState.Loaded);
        if (_logger is { } loadedLogger)
        {
            var version = instance.Manifest.Version.ToString();
            ExtensionLogMessages.ExtensionLoaded(
                loadedLogger,
                instance.Manifest.Id,
                version,
                instance.Handlers.Count,
                instance.Fallback is not null);
        }
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
        catch (Exception exception)
        {
            // Lifecycle publication is best effort and must not alter extension transitions.
            if (_logger is { } logger)
            {
                ExtensionLogMessages.ExtensionLifecyclePublicationFailed(
                    logger,
                    exception,
                    instance.Manifest.Id,
                    nameof(PublishExtensionState));
            }
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
        if (_logger is { } failureLogger && instance is not null)
        {
            var failureCode = category.ToString();
            ExtensionLogMessages.ExtensionFailureDetails(
                failureLogger,
                exception,
                instance.Manifest.Id,
                failureCode);
        }

        if (instance is null || instance.RecordFailure(category, exception))
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
                        GetTurnstile(instance.Manifest.Id).SetCurrent(null);
                    }

                    shouldStop = true;
                }
            }

            if (!shouldStop)
            {
                return;
            }

            if (_logger is { } failureLogger)
            {
                var version = instance.Manifest.Version.ToString();
                ExtensionLogMessages.ExtensionStoppedAfterFailures(
                    failureLogger,
                    instance.Manifest.Id,
                    version);
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

    private sealed record CandidateResult(
        bool Succeeded,
        ExtensionFailureCode FailureCode,
        ExtensionErrorDetail? FailureDetail,
        ExtensionInstance? Instance)
    {
        internal static CandidateResult Success(ExtensionInstance instance) =>
            new(true, ExtensionFailureCode.None, null, instance);

        internal static CandidateResult Failure(ExtensionFailureCode code, ExtensionErrorDetail failureDetail) =>
            new(false, code, failureDetail, null);
    }

    private sealed record HandlerBinding(
        ExtensionInstance Instance,
        IExtensionHandler? Handler,
        IExtensionStreamingHandler? StreamingHandler,
        IExtensionFallback? Fallback);
}
