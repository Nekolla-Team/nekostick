using System.Collections.Immutable;
using System.Threading;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostServiceLifecycleManager
{
    private readonly SemaphoreSlim _graphTransactionGate = new(1, 1);
    private readonly object _graphPreparationGate = new();
    private readonly Dictionary<CancellationTokenSource, long> _graphPreparationTokens = [];
    private long _failedGraphConfigurationVersion = -1;
    private bool _failedGraphDatabaseUnavailable;
    private bool _failedGraphRetryableTransient;

    private readonly record struct GraphRefreshOutcome(
        bool Required,
        bool Committed,
        bool DatabaseUnavailable,
        bool Superseded,
        ImmutableHashSet<Guid> AffectedServiceIds,
        bool RetryableTransientProvenance = false)
    {
        internal static GraphRefreshOutcome None { get; } = new(
            false,
            false,
            false,
            false,
            ImmutableHashSet<Guid>.Empty);
    }

    private sealed record ActiveGraphPlan(
        HostConfigurationSnapshot Snapshot,
        ImmutableHashSet<Guid> RootServiceIds,
        ImmutableHashSet<Guid> RestartServiceIds,
        ImmutableHashSet<Guid> ServiceIds,
        ImmutableArray<Guid> PreparationOrder,
        ImmutableDictionary<Guid, ServiceGeneration> OldGenerations,
        ImmutableDictionary<Guid, ServiceGeneration> ObservedGenerations,
        ImmutableDictionary<Guid, ServiceConfiguration> Configurations,
        bool IsValid);

    private async ValueTask<HostServiceReadinessResult> EnsureReadyWithGraphRefreshAsync(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        var outcome = await RefreshChangedActiveGraphAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var latest = LatestSnapshot(snapshot);
        if (outcome.Superseded && latest.Version > snapshot.Version)
        {
            return await EnsureReadyWithGraphRefreshAsync(latest, serviceId, cancellationToken).ConfigureAwait(false);
        }

        if (outcome.Required && outcome.AffectedServiceIds.Contains(serviceId) && !outcome.Committed)
        {

            return new(
                serviceId,
                latest.Version,
                outcome.DatabaseUnavailable
                    ? HostServiceReadinessStatus.DatabaseUnavailable
                    : HostServiceReadinessStatus.Unavailable,
                databaseUnavailableProvenance: outcome.DatabaseUnavailable,
                retryableTransientProvenance: outcome.RetryableTransientProvenance);
        }

        return await EnsureReadyAsync(
            latest,
            serviceId,
            ImmutableHashSet<Guid>.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphRefreshOutcome> RefreshChangedActiveGraphAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken,
        bool forceRetry = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveSnapshot = LatestSnapshot(snapshot);
        var operationCancellation = RegisterGraphPreparation(effectiveSnapshot.Version);
        var operation = RefreshChangedActiveGraphCoreAsync(
            effectiveSnapshot,
            forceRetry,
            operationCancellation.Token);
        try
        {
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (operation.IsCompleted)
            {
                DisposeGraphPreparation(operationCancellation);
            }
            else
            {
                _ = operation.ContinueWith(
                    static (_, state) =>
                    {
                        var (manager, source) = ((HostServiceLifecycleManager Manager, CancellationTokenSource Source))state!;
                        manager.DisposeGraphPreparation(source);
                    },
                    (this, operationCancellation),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private async Task WaitForGraphTransactionAsync(CancellationToken cancellationToken)
    {
        await _graphTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _graphTransactionGate.Release();
    }

    private void DisposeGraphPreparation(CancellationTokenSource source)
    {
        lock (_graphPreparationGate)
        {
            _graphPreparationTokens.Remove(source);
        }

        source.Dispose();
    }

    private CancellationTokenSource RegisterGraphPreparation(long configurationVersion)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        CancellationTokenSource[] superseded;
        lock (_graphPreparationGate)
        {
            superseded = _graphPreparationTokens
                .Where(entry => entry.Value < configurationVersion && !entry.Key.IsCancellationRequested)
                .Select(static entry => entry.Key)
                .ToArray();
            _graphPreparationTokens.Add(source, configurationVersion);
        }

        CancelGraphPreparations(superseded);
        return source;
    }

    private void CancelGraphPreparationsForExplicitRestart(long configurationVersion)
    {
        CancellationTokenSource[] sources;
        lock (_graphPreparationGate)
        {
            sources = _graphPreparationTokens
                .Where(entry => entry.Value <= configurationVersion && !entry.Key.IsCancellationRequested)
                .Select(static entry => entry.Key)
                .ToArray();
        }

        CancelGraphPreparations(sources);
    }

    private static void CancelGraphPreparations(IEnumerable<CancellationTokenSource> sources)
    {
        foreach (var source in sources)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task<GraphRefreshOutcome> RefreshChangedActiveGraphCoreAsync(
        HostConfigurationSnapshot snapshot,
        bool forceRetry,
        CancellationToken cancellationToken)
    {
        try
        {
            await _graphTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(true, false, false, true, ImmutableHashSet<Guid>.Empty);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = LatestSnapshot(snapshot);
            if (latest.Version != snapshot.Version)
            {
                return new(true, false, false, true, ImmutableHashSet<Guid>.Empty);
            }

            var plan = CreateActiveGraphPlan(latest);
            if (plan is null)
            {
                return GraphRefreshOutcome.None;
            }

            if (!forceRetry && _failedGraphConfigurationVersion == latest.Version)
            {
                return new(true, false, _failedGraphDatabaseUnavailable, false, plan.ServiceIds, _failedGraphRetryableTransient);
            }

            if (!plan.IsValid)
            {
                _failedGraphConfigurationVersion = latest.Version;
                _failedGraphDatabaseUnavailable = false;
                _failedGraphRetryableTransient = false;
                return new(true, false, false, false, plan.ServiceIds);
            }

            var candidates = new Dictionary<Guid, ServiceGeneration>();
            var projectedBindings = new Dictionary<Guid, ImmutableDictionary<Guid, ServiceDependencyBinding>>();
            var databaseUnavailable = false;
            var retryableTransient = false;
            try
            {
                foreach (var serviceId in plan.PreparationOrder)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (LatestSnapshot(latest).Version != latest.Version)
                    {
                        await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                        return new(true, false, false, true, plan.ServiceIds);
                    }

                    var service = plan.Configurations[serviceId];
                    if (!TryBuildGraphDependencyBindings(
                            latest,
                            service,
                            plan,
                            candidates,
                            out var serviceBindings))
                    {
                        await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                        MarkGraphFailure(latest.Version, false);
                        return new(true, false, false, false, plan.ServiceIds);
                    }

                    var oldGeneration = plan.OldGenerations[serviceId];
                    if (!plan.RestartServiceIds.Contains(serviceId) &&
                        oldGeneration.Lease is { } oldLease &&
                        TryResolveLaunchInputs(service, oldLease.Port, serviceBindings, out var launchInputs) &&
                        LaunchInputsEqual(oldGeneration, launchInputs))
                    {
                        projectedBindings[serviceId] = serviceBindings;
                        continue;
                    }

                    var slot = _slots[serviceId];
                    var canPrepare = false;
                    lock (_lifecycleGate)
                    {
                        lock (slot.Gate)
                        {
                            if (ReferenceEquals(slot.Active, oldGeneration) &&
                                slot.Starting is null &&
                                slot.Startup is null &&
                                !slot.GraphPreparation)
                            {
                                slot.GraphPreparation = true;
                                canPrepare = true;
                            }
                        }
                    }

                    if (!canPrepare)
                    {
                        await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                        return new(true, false, false, true, plan.ServiceIds);
                    }

                    var attemptNumber = slot.ReserveStartAttemptNumber();
                    var started = await StartGenerationAsync(
                        slot,
                        latest,
                        service,
                        attemptNumber,
                        serviceBindings,
                        cancellationToken,
                        graphPreparation: true).ConfigureAwait(false);
                    databaseUnavailable |= started.DatabaseUnavailableProvenance;
                    retryableTransient |= started.RetryableTransientProvenance;
                    if (started.Generation is { } candidate)
                    {
                        candidates[serviceId] = candidate;
                    }

                    if (started.Generation is not { Ready: true } readyCandidate ||
                        !TryValidatePreparedGeneration(readyCandidate, serviceId, latest, plan, candidates))
                    {
                        await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested || LatestSnapshot(latest).Version != latest.Version)
                        {
                            return new(true, false, false, true, plan.ServiceIds);
                        }

                        if (!retryableTransient)
                        {
                            MarkGraphFailure(latest.Version, databaseUnavailable);
                        }
                        else
                        {
                            MarkGraphFailure(latest.Version, databaseUnavailable, retryableTransient);
                        }

                        return new(
                            true,
                            false,
                            databaseUnavailable,
                            false,
                            plan.ServiceIds,
                            retryableTransient);
                    }

                    projectedBindings[serviceId] = readyCandidate.DependencyBindings;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var committed = await CommitPreparedGraphAsync(
                    plan,
                    candidates,
                    projectedBindings,
                    cancellationToken).ConfigureAwait(false);
                if (!committed)
                {
                    await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                    var superseded = LatestSnapshot(latest).Version != latest.Version;
                    if (!superseded)
                    {
                        MarkGraphFailure(latest.Version, false);
                        _failedGraphRetryableTransient = false;
                    }

                    return new(true, false, false, superseded, plan.ServiceIds);
                }

                _failedGraphConfigurationVersion = -1;
                _failedGraphDatabaseUnavailable = false;
                _failedGraphRetryableTransient = false;
                foreach (var serviceId in plan.PreparationOrder)
                {
                    if (candidates.TryGetValue(serviceId, out var candidate))
                    {
                        HostLogMessages.ServiceReady(_logger, serviceId, candidate.SnapshotVersion);
                        PublishServiceState(serviceId, candidate.SnapshotVersion, "ready");
                    }
                }

                await RetireOldGraphAsync(plan, candidates).ConfigureAwait(false);
                return new(true, true, false, false, plan.ServiceIds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                return new(true, false, false, true, plan.ServiceIds);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(RefreshChangedActiveGraphCoreAsync));
                await RollbackGraphCandidatesAsync(plan, candidates).ConfigureAwait(false);
                if (!retryableTransient)
                {
                    MarkGraphFailure(latest.Version, databaseUnavailable);
                }
                else
                {
                    MarkGraphFailure(latest.Version, databaseUnavailable, retryableTransient);
                }

                return new(
                    true,
                    false,
                    databaseUnavailable,
                    false,
                    plan.ServiceIds,
                    retryableTransient);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(true, false, false, true, ImmutableHashSet<Guid>.Empty);
        }
        finally
        {
            _graphTransactionGate.Release();
        }
    }

    private void MarkGraphFailure(long configurationVersion, bool databaseUnavailable, bool retryableTransient = false)
    {
        _failedGraphConfigurationVersion = configurationVersion;
        _failedGraphDatabaseUnavailable = databaseUnavailable;
        _failedGraphRetryableTransient = retryableTransient;
    }

    /// <summary>Computes the graph refresh redrive entry for one lifecycle tick.</summary>
    /// <param name="snapshotVersion">The configuration version observed by the tick.</param>
    /// <returns><c>true</c> when the current version failed with a retryable transient outcome.</returns>
    internal bool HasFailedGraphTransientRetry(long snapshotVersion) =>
        Volatile.Read(ref _failedGraphConfigurationVersion) == snapshotVersion &&
        Volatile.Read(ref _failedGraphRetryableTransient);

    /// <summary>Re-drives the failed graph refresh once per lifecycle tick for a retryable transient outcome.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal async Task RetryFailedGraphTransientAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var snapshot = _snapshotHolder.Current;
        if (snapshot is null ||
            !HasFailedGraphTransientRetry(snapshot.Version) ||
            !_runtimeState.NewServicesAllowed)
        {
            return;
        }

        await RefreshChangedActiveGraphAsync(snapshot, cancellationToken, forceRetry: true).ConfigureAwait(false);
    }

    private ActiveGraphPlan? CreateActiveGraphPlan(HostConfigurationSnapshot snapshot)
    {
        var active = ImmutableDictionary.CreateBuilder<Guid, ServiceGeneration>();
        lock (_lifecycleGate)
        {
            foreach (var pair in _slots)
            {
                lock (pair.Value.Gate)
                {
                    if (pair.Value.Active is { Ready: true } generation)
                    {
                        active[pair.Key] = generation;
                    }
                }
            }
        }

        if (active.Count == 0)
        {
            return null;
        }

        var activeGenerations = active.ToImmutable();
        var configurations = ImmutableDictionary.CreateBuilder<Guid, ServiceConfiguration>();
        var dependenciesByService = ImmutableDictionary.CreateBuilder<Guid, ImmutableHashSet<Guid>>();
        var roots = ImmutableHashSet.CreateBuilder<Guid>();
        var restartRoots = ImmutableHashSet.CreateBuilder<Guid>();
        var committedView = _endpointPublisher.CommittedView;
        foreach (var (serviceId, generation) in activeGenerations)
        {
            var configured = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
            if (configured is not { Enabled: true } || !IsServiceEnabledForSnapshot(snapshot, serviceId))
            {
                continue;
            }

            configurations[serviceId] = configured;
            var configuredDependencies = ServiceLaunchTemplate.ExtractDependencies(
                configured.ArgumentList.Cast<string?>().Concat(configured.Environment.Values));
            dependenciesByService[serviceId] = configuredDependencies;
            var currentOwner = GetServiceOwner(serviceId);
            var dependenciesAvailable = TryBuildActiveDependencyBindings(
                snapshot,
                configuredDependencies,
                activeGenerations,
                out var dependencyBindings);
            var launchInputsMatch = generation.Lease is { } lease &&
                dependenciesAvailable &&
                TryResolveLaunchInputs(configured, lease.Port, dependencyBindings, out var launchInputs) &&
                LaunchInputsEqual(generation, launchInputs);
            var restartRequired = generation.Configuration.Version != configured.Version ||
                !configuredDependencies.SetEquals(generation.DependencyBindings.Keys) ||
                !string.Equals(generation.OwnerExtensionId, currentOwner, StringComparison.Ordinal) ||
                !launchInputsMatch;
            if (restartRequired)
            {
                restartRoots.Add(serviceId);
            }

            if (restartRequired ||
                !CommittedServiceMatchesActive(
                    snapshot,
                    configured,
                    configuredDependencies,
                    generation,
                    activeGenerations,
                    committedView))
            {
                roots.Add(serviceId);
            }
        }

        if (roots.Count == 0)
        {
            return null;
        }

        var affected = roots.ToImmutable().ToBuilder();
        var restartAffected = restartRoots.ToImmutable().ToBuilder();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var serviceId in activeGenerations.Keys)
            {
                if (configurations.ContainsKey(serviceId) &&
                    dependenciesByService.TryGetValue(serviceId, out var serviceDependencies) &&
                    serviceDependencies.Any(restartAffected.Contains))
                {
                    changed |= affected.Add(serviceId);
                    changed |= restartAffected.Add(serviceId);
                }
            }
        }

        var serviceIds = affected.ToImmutable();
        var plannedConfigurations = ImmutableDictionary.CreateBuilder<Guid, ServiceConfiguration>();
        var observedIds = serviceIds.ToBuilder();
        var valid = true;
        foreach (var serviceId in serviceIds)
        {
            if (!configurations.TryGetValue(serviceId, out var configured) ||
                !dependenciesByService.TryGetValue(serviceId, out var serviceDependencies))
            {
                valid = false;
                continue;
            }

            plannedConfigurations[serviceId] = configured;
            foreach (var dependencyId in serviceDependencies)
            {
                observedIds.Add(dependencyId);
                var dependencyConfiguration = snapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
                if (dependencyConfiguration is not { Enabled: true } ||
                    !IsServiceEnabledForSnapshot(snapshot, dependencyId) ||
                    !activeGenerations.ContainsKey(dependencyId))
                {
                    valid = false;
                }
            }
        }

        var configurationMap = plannedConfigurations.ToImmutable();
        var preparationOrder = valid
            ? TopologicalOrder(configurationMap, serviceIds)
            : ImmutableArray<Guid>.Empty;
        if (preparationOrder.Length != serviceIds.Count)
        {
            valid = false;
        }

        var oldGenerations = serviceIds
            .Where(activeGenerations.ContainsKey)
            .ToImmutableDictionary(serviceId => serviceId, serviceId => activeGenerations[serviceId]);
        var observedGenerations = observedIds
            .Where(activeGenerations.ContainsKey)
            .ToImmutableDictionary(serviceId => serviceId, serviceId => activeGenerations[serviceId]);
        if (oldGenerations.Count != serviceIds.Count || observedGenerations.Count != observedIds.Count)
        {
            valid = false;
        }

        return new ActiveGraphPlan(
            snapshot,
            roots.ToImmutable(),
            restartRoots.ToImmutable(),
            serviceIds,
            preparationOrder,
            oldGenerations,
            observedGenerations,
            configurationMap,
            valid);
    }

    private static bool HasExistingCommittedGraphState(
        ActiveGraphPlan plan,
        HostServiceCommittedGraphView previous)
    {
        foreach (var serviceId in plan.ServiceIds)
        {
            if (previous.Services.ContainsKey(serviceId))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBuildActiveDependencyBindings(
        HostConfigurationSnapshot snapshot,
        ImmutableHashSet<Guid> dependencyIds,
        ImmutableDictionary<Guid, ServiceGeneration> active,
        out ImmutableDictionary<Guid, ServiceDependencyBinding> bindings)
    {
        var result = ImmutableDictionary.CreateBuilder<Guid, ServiceDependencyBinding>();
        foreach (var dependencyId in dependencyIds)
        {
            var dependencyConfiguration = snapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
            if (dependencyConfiguration is not { Enabled: true } ||
                !IsServiceEnabledForSnapshot(snapshot, dependencyId) ||
                !active.TryGetValue(dependencyId, out var generation) ||
                !TryCreateCandidateDependencyBinding(generation, out var binding) ||
                binding.ServiceId != dependencyId)
            {
                bindings = ImmutableDictionary<Guid, ServiceDependencyBinding>.Empty;
                return false;
            }

            result[dependencyId] = binding;
        }

        bindings = result.ToImmutable();
        return true;
    }

    private bool TryBuildGraphDependencyBindings(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates,
        out ImmutableDictionary<Guid, ServiceDependencyBinding> bindings)
    {
        var result = ImmutableDictionary.CreateBuilder<Guid, ServiceDependencyBinding>();
        foreach (var dependencyId in ServiceLaunchTemplate.ExtractDependencies(
                     service.ArgumentList.Cast<string?>().Concat(service.Environment.Values)))
        {
            var dependencyConfiguration = snapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
            if (dependencyConfiguration is not { Enabled: true } ||
                !IsServiceEnabledForSnapshot(snapshot, dependencyId))
            {
                bindings = ImmutableDictionary<Guid, ServiceDependencyBinding>.Empty;
                return false;
            }

            if (candidates.TryGetValue(dependencyId, out var candidateDependency))
            {
                if (!TryCreateCandidateDependencyBinding(candidateDependency, out var candidateBinding) ||
                    candidateBinding.ServiceVersion != dependencyConfiguration.Version ||
                    !string.Equals(candidateBinding.OwnerExtensionId, GetServiceOwner(dependencyId), StringComparison.Ordinal))
                {
                    bindings = ImmutableDictionary<Guid, ServiceDependencyBinding>.Empty;
                    return false;
                }

                result[dependencyId] = candidateBinding;
                continue;
            }

            if (!plan.ObservedGenerations.TryGetValue(dependencyId, out var activeDependency) ||
                !TryCreateCandidateDependencyBinding(activeDependency, out var activeBinding) ||
                activeBinding.ServiceVersion != dependencyConfiguration.Version ||
                !string.Equals(activeBinding.OwnerExtensionId, GetServiceOwner(dependencyId), StringComparison.Ordinal) ||
                !_slots.TryGetValue(dependencyId, out var dependencySlot))
            {
                bindings = ImmutableDictionary<Guid, ServiceDependencyBinding>.Empty;
                return false;
            }

            lock (dependencySlot.Gate)
            {
                if (!ReferenceEquals(dependencySlot.Active, activeDependency))
                {
                    bindings = ImmutableDictionary<Guid, ServiceDependencyBinding>.Empty;
                    return false;
                }
            }

            result[dependencyId] = activeBinding;
        }

        bindings = result.ToImmutable();
        return true;
    }

    private static bool TryResolveLaunchInputs(
        ServiceConfiguration service,
        int port,
        ImmutableDictionary<Guid, ServiceDependencyBinding> dependencyBindings,
        out (ImmutableArray<string> Arguments, ImmutableDictionary<string, string> Environment) launchInputs)
    {
        try
        {
            launchInputs = ResolveLaunchInputs(
                service,
                port,
                (dependencyId, name) => dependencyBindings.TryGetValue(dependencyId, out var binding) &&
                    binding.ResolvedEnvironment.TryGetValue(name, out var value)
                    ? value
                    : null);
            return true;
        }
        catch (Exception)
        {
            launchInputs = (ImmutableArray<string>.Empty, ImmutableDictionary<string, string>.Empty);
            return false;
        }
    }

    private static bool LaunchInputsEqual(
        ServiceGeneration generation,
        (ImmutableArray<string> Arguments, ImmutableDictionary<string, string> Environment) launchInputs)
    {
        if (!generation.ResolvedArguments.SequenceEqual(launchInputs.Arguments, StringComparer.Ordinal) ||
            generation.ResolvedEnvironment.Count != launchInputs.Environment.Count)
        {
            return false;
        }

        foreach (var (name, value) in generation.ResolvedEnvironment)
        {
            if (!launchInputs.Environment.TryGetValue(name, out var resolvedValue) ||
                !string.Equals(value, resolvedValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool CommittedServiceMatchesActive(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration configured,
        ImmutableHashSet<Guid> dependencyIds,
        ServiceGeneration generation,
        ImmutableDictionary<Guid, ServiceGeneration> active,
        HostServiceCommittedGraphView view)
    {
        var now = DateTimeOffset.UtcNow;
        if (!view.Services.TryGetValue(configured.Id, out var committed) ||
            generation.Configuration.Version != configured.Version ||
            committed.ServiceVersion != configured.Version ||
            committed.GenerationId != generation.GenerationId ||
            committed.Endpoint.GenerationId != generation.GenerationId ||
            generation.Lease is not { } lease ||
            committed.Endpoint.Port != lease.Port ||
            !committed.Endpoint.IsActive(now) ||
            !ReferenceEquals(committed.ResolvedEnvironment, generation.ResolvedEnvironment) ||
            !string.Equals(generation.OwnerExtensionId, GetServiceOwner(configured.Id), StringComparison.Ordinal) ||
            !string.Equals(committed.Endpoint.OwnerExtensionId, generation.OwnerExtensionId, StringComparison.Ordinal) ||
            !committed.DependencyBindings.Keys.ToImmutableHashSet().SetEquals(dependencyIds))
        {
            return false;
        }

        foreach (var dependencyId in dependencyIds)
        {
            var dependencyConfiguration = snapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
            if (dependencyConfiguration is not { Enabled: true } ||
                !IsServiceEnabledForSnapshot(snapshot, dependencyId) ||
                !active.TryGetValue(dependencyId, out var dependencyGeneration) ||
                dependencyGeneration.Configuration.Version != dependencyConfiguration.Version ||
                !string.Equals(
                    dependencyGeneration.OwnerExtensionId,
                    GetServiceOwner(dependencyId),
                    StringComparison.Ordinal) ||
                !TryCreateCandidateDependencyBinding(dependencyGeneration, out var dependencyBinding) ||
                !committed.DependencyBindings.TryGetValue(dependencyId, out var committedBinding) ||
                committedBinding.ServiceVersion != dependencyBinding.ServiceVersion ||
                committedBinding.GenerationId != dependencyBinding.GenerationId ||
                committedBinding.Port != dependencyBinding.LeaseIdentity.Port ||
                !ReferenceEquals(committedBinding.ResolvedEnvironment, dependencyBinding.ResolvedEnvironment) ||
                !string.Equals(
                    committedBinding.OwnerExtensionId,
                    dependencyBinding.OwnerExtensionId,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryPreserveCommittedDependencyBindings(
        ServiceGeneration generation,
        ImmutableDictionary<Guid, HostServiceCommittedDependencyBinding> committedBindings)
    {
        if (committedBindings.Count != generation.DependencyBindings.Count)
        {
            return false;
        }

        foreach (var (dependencyId, committedBinding) in committedBindings)
        {
            if (committedBinding.ServiceId != dependencyId ||
                !generation.DependencyBindings.ContainsKey(dependencyId) ||
                !_slots.TryGetValue(dependencyId, out var slot))
            {
                return false;
            }

            lock (slot.Gate)
            {
                if (slot.Active is not { Ready: true } dependencyGeneration ||
                    !TryCreateCandidateDependencyBinding(dependencyGeneration, out var currentBinding) ||
                    currentBinding.ServiceId != dependencyId ||
                    currentBinding.ServiceVersion != committedBinding.ServiceVersion ||
                    currentBinding.GenerationId != committedBinding.GenerationId ||
                    currentBinding.LeaseIdentity.Port != committedBinding.Port ||
                    !ReferenceEquals(currentBinding.ResolvedEnvironment, committedBinding.ResolvedEnvironment) ||
                    !string.Equals(
                        currentBinding.OwnerExtensionId,
                        committedBinding.OwnerExtensionId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        currentBinding.OwnerExtensionId,
                        GetServiceOwner(dependencyId),
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ImmutableArray<Guid> TopologicalOrder(
        ImmutableDictionary<Guid, ServiceConfiguration> configurations,
        ImmutableHashSet<Guid> serviceIds)
    {
        var dependencies = configurations.ToDictionary(
            static pair => pair.Key,
            static pair => ServiceLaunchTemplate.ExtractDependencies(
                pair.Value.ArgumentList.Cast<string?>().Concat(pair.Value.Environment.Values)));
        var remaining = serviceIds.ToHashSet();
        var ordered = ImmutableArray.CreateBuilder<Guid>(serviceIds.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(serviceId => dependencies[serviceId].All(dependency =>
                    !serviceIds.Contains(dependency) || !remaining.Contains(dependency)))
                .Order()
                .ToArray();
            if (ready.Length == 0)
            {
                return ImmutableArray<Guid>.Empty;
            }

            foreach (var serviceId in ready)
            {
                ordered.Add(serviceId);
                remaining.Remove(serviceId);
            }
        }

        return ordered.ToImmutable();
    }

    private bool TryCreateCandidateDependencyBinding(
        ServiceGeneration generation,
        out ServiceDependencyBinding binding)
    {
        var lease = generation.Lease;
        if (!generation.Ready ||
            lease is null ||
            generation.Supervisor.Lease is not { } supervisorLease ||
            !IsValidDependencyLease(lease, generation.Configuration.Id, generation.GenerationId, DateTimeOffset.UtcNow) ||
            GetDependencyLeaseIdentity(lease) != GetDependencyLeaseIdentity(supervisorLease))
        {
            binding = null!;
            return false;
        }

        binding = new ServiceDependencyBinding(
            generation.Configuration.Id,
            generation.Configuration.Version,
            generation.GenerationId,
            generation.ResolvedEnvironment,
            GetDependencyLeaseIdentity(lease),
            generation.OwnerExtensionId);
        return true;
    }

    private bool TryValidatePreparedGeneration(
        ServiceGeneration generation,
        Guid serviceId,
        HostConfigurationSnapshot snapshot,
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates)
    {
        var configured = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        var expectedDependencies = configured is null
            ? ImmutableHashSet<Guid>.Empty
            : ServiceLaunchTemplate.ExtractDependencies(
                configured.ArgumentList.Cast<string?>().Concat(configured.Environment.Values));
        var now = DateTimeOffset.UtcNow;
        if (!generation.Ready ||
            generation.Configuration.Id != serviceId ||
            generation.Configuration.Version != configured?.Version ||
            !expectedDependencies.SetEquals(generation.DependencyBindings.Keys) ||
            generation.Supervisor.Snapshot.ObservedLifecycle != ServiceLifecycleState.Running ||
            generation.Supervisor.ActiveProcessInstance is null ||
            generation.Lease is not { } lease ||
            generation.Supervisor.Lease is not { } supervisorLease ||
            !IsValidDependencyLease(lease, serviceId, generation.GenerationId, now) ||
            GetDependencyLeaseIdentity(lease) != GetDependencyLeaseIdentity(supervisorLease))
        {
            return false;
        }

        foreach (var binding in generation.DependencyBindings.Values)
        {
            if (!TryValidatePreparedBinding(binding, plan, candidates))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryValidatePreparedBinding(
        ServiceDependencyBinding binding,
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates)
    {
        if (candidates.TryGetValue(binding.ServiceId, out var candidateDependency))
        {
            return TryCreateCandidateDependencyBinding(candidateDependency, out var candidateBinding) &&
                DependencyBindingsEqual(binding, candidateBinding);
        }

        if (!plan.ObservedGenerations.TryGetValue(binding.ServiceId, out var observedDependency) ||
            !_slots.TryGetValue(binding.ServiceId, out var slot))
        {
            return false;
        }

        lock (slot.Gate)
        {
            return ReferenceEquals(slot.Active, observedDependency) &&
                TryCreateCandidateDependencyBinding(observedDependency, out var activeBinding) &&
                DependencyBindingsEqual(binding, activeBinding);
        }
    }

    private static bool DependencyBindingsEqual(
        ServiceDependencyBinding expected,
        ServiceDependencyBinding actual) =>
        expected.ServiceId == actual.ServiceId &&
        expected.ServiceVersion == actual.ServiceVersion &&
        expected.GenerationId == actual.GenerationId &&
        ReferenceEquals(expected.ResolvedEnvironment, actual.ResolvedEnvironment) &&
        expected.LeaseIdentity == actual.LeaseIdentity &&
        string.Equals(expected.OwnerExtensionId, actual.OwnerExtensionId, StringComparison.Ordinal);

    private async Task<bool> CommitPreparedGraphAsync(
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates,
        Dictionary<Guid, ImmutableDictionary<Guid, ServiceDependencyBinding>> projectedBindings,
        CancellationToken cancellationToken)
    {
        using var suspension = await _admissionCoordinator
            .SuspendAsync(plan.ServiceIds, cancellationToken)
            .ConfigureAwait(false);
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = LatestSnapshot(plan.Snapshot);
            if (latest.Version != plan.Snapshot.Version)
            {
                return false;
            }

            lock (_lifecycleGate)
            {
                var currentPlan = CreateActiveGraphPlan(latest);
                if (!GraphPlansMatch(plan, currentPlan))
                {
                    return false;
                }

                var orderedSlots = plan.ObservedGenerations.Keys
                    .Union(plan.ServiceIds)
                    .Order()
                    .Select(serviceId => (ServiceId: serviceId, Slot: _slots[serviceId]))
                    .ToArray();
                var entered = 0;
                try
                {
                    foreach (var item in orderedSlots)
                    {
                        Monitor.Enter(item.Slot.Gate);
                        entered++;
                    }

                    if (!ValidatePreparedGraph(plan, candidates, projectedBindings, latest))
                    {
                        return false;
                    }

                    var previous = _endpointPublisher.CommittedView;
                    var services = previous.Services.ToBuilder();
                    var endpoints = previous.Endpoints.ToBuilder();
                    var runtimeSnapshots = previous.RuntimeSnapshots.ToBuilder();
                    var runtimePublications = new List<(HostServiceRuntimeSnapshot Snapshot, long ServiceVersion)>(candidates.Count);
                    foreach (var serviceId in plan.PreparationOrder)
                    {
                        var generation = candidates.TryGetValue(serviceId, out var candidate)
                            ? candidate
                            : plan.OldGenerations[serviceId];
                        var endpoint = CreateEndpointLease(generation);
                        HostServiceRuntimeSnapshot runtime;
                        if (candidate is not null)
                        {
                            runtime = CreateGraphRuntimeSnapshot(
                                candidate,
                                plan.Snapshot.Version,
                                runtimeSnapshots.TryGetValue(serviceId, out var priorCandidateRuntime)
                                    ? priorCandidateRuntime
                                    : null);
                            runtimePublications.Add((runtime, candidate.Configuration.Version));
                        }
                        else if (runtimeSnapshots.TryGetValue(serviceId, out var priorRuntime) &&
                            priorRuntime.GenerationId == generation.GenerationId)
                        {
                            runtime = priorRuntime;
                        }
                        else
                        {
                            runtime = CreateGraphRuntimeSnapshot(
                                generation,
                                plan.Snapshot.Version,
                                runtimeSnapshots.TryGetValue(serviceId, out var priorGenerationRuntime)
                                    ? priorGenerationRuntime
                                    : null);
                            runtimePublications.Add((runtime, generation.Configuration.Version));
                        }

                        var dependencyBindings = projectedBindings[serviceId].Values.ToImmutableDictionary(
                            static binding => binding.ServiceId,
                            binding => new HostServiceCommittedDependencyBinding(
                                binding.ServiceId,
                                binding.ServiceVersion,
                                binding.GenerationId,
                                binding.LeaseIdentity.Port,
                                binding.ResolvedEnvironment,
                                binding.OwnerExtensionId));
                        services[serviceId] = new HostServiceCommittedService(
                            plan.Configurations[serviceId].Version,
                            generation.GenerationId,
                            endpoint,
                            dependencyBindings,
                            generation.ResolvedEnvironment,
                            runtime);
                        endpoints[serviceId] = endpoint;
                        runtimeSnapshots[serviceId] = runtime;
                    }

                    var nextView = new HostServiceCommittedGraphView(
                        plan.Snapshot.Version,
                        services.ToImmutable(),
                        endpoints.ToImmutable(),
                        runtimeSnapshots.ToImmutable());
                    foreach (var serviceId in plan.PreparationOrder)
                    {
                        if (!candidates.TryGetValue(serviceId, out var candidate))
                        {
                            continue;
                        }

                        var slot = _slots[serviceId];
                        var old = plan.OldGenerations[serviceId];
                        _retiringGenerations.TryAdd(old, new RetiringGenerationState());
                        slot.Active = candidate;
                        if (ReferenceEquals(slot.Starting, candidate))
                        {
                            slot.Starting = null;
                        }

                        slot.GraphPreparation = false;
                        candidate.GraphPreparation = false;
                        candidate.Ready = true;
                    }

                    _runtimeRegistry.PublishCommittedGraph(nextView, runtimePublications);
                    return true;
                }
                finally
                {
                    for (var index = entered - 1; index >= 0; index--)
                    {
                        Monitor.Exit(orderedSlots[index].Slot.Gate);
                    }
                }
            }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private static bool GraphPlansMatch(ActiveGraphPlan expected, ActiveGraphPlan? actual)
    {
        if (actual is null ||
            !actual.IsValid ||
            !expected.RootServiceIds.SetEquals(actual.RootServiceIds) ||
            !expected.RestartServiceIds.SetEquals(actual.RestartServiceIds) ||
            !expected.ServiceIds.SetEquals(actual.ServiceIds) ||
            !expected.PreparationOrder.SequenceEqual(actual.PreparationOrder) ||
            expected.OldGenerations.Count != actual.OldGenerations.Count ||
            expected.ObservedGenerations.Count != actual.ObservedGenerations.Count)
        {
            return false;
        }

        return expected.OldGenerations.All(pair =>
                actual.OldGenerations.TryGetValue(pair.Key, out var generation) &&
                ReferenceEquals(pair.Value, generation)) &&
            expected.ObservedGenerations.All(pair =>
                actual.ObservedGenerations.TryGetValue(pair.Key, out var generation) &&
                ReferenceEquals(pair.Value, generation));
    }

    private bool ValidatePreparedGraph(
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates,
        Dictionary<Guid, ImmutableDictionary<Guid, ServiceDependencyBinding>> projectedBindings,
        HostConfigurationSnapshot latest)
    {
        if (candidates.Keys.Any(serviceId => !plan.ServiceIds.Contains(serviceId)) ||
            projectedBindings.Count != plan.ServiceIds.Count)
        {
            return false;
        }

        foreach (var (serviceId, observedGeneration) in plan.ObservedGenerations)
        {
            if (!_slots.TryGetValue(serviceId, out var observedSlot) ||
                !ReferenceEquals(observedSlot.Active, observedGeneration) ||
                observedGeneration.Supervisor.Snapshot.ObservedLifecycle != ServiceLifecycleState.Running ||
                observedGeneration.Supervisor.ActiveProcessInstance is null)
            {
                return false;
            }
        }

        foreach (var serviceId in plan.PreparationOrder)
        {
            var configured = plan.Configurations[serviceId];
            var expectedDependencies = ServiceLaunchTemplate.ExtractDependencies(
                configured.ArgumentList.Cast<string?>().Concat(configured.Environment.Values));
            if (!projectedBindings.TryGetValue(serviceId, out var bindings) ||
                !expectedDependencies.SetEquals(bindings.Keys))
            {
                return false;
            }

            foreach (var binding in bindings.Values)
            {
                if (!TryValidatePreparedBinding(binding, plan, candidates))
                {
                    return false;
                }
            }

            var slot = _slots[serviceId];
            var oldGeneration = plan.OldGenerations[serviceId];
            if (candidates.TryGetValue(serviceId, out var candidate))
            {
                if (!TryValidatePreparedGeneration(candidate, serviceId, latest, plan, candidates) ||
                    candidate.DependencyBindings.Count != bindings.Count ||
                    candidate.DependencyBindings.Any(pair =>
                        !bindings.TryGetValue(pair.Key, out var binding) ||
                        !DependencyBindingsEqual(pair.Value, binding)) ||
                    !ReferenceEquals(slot.Active, oldGeneration) ||
                    !ReferenceEquals(slot.Starting, candidate) ||
                    !slot.GraphPreparation)
                {
                    return false;
                }

                continue;
            }

            if (plan.RestartServiceIds.Contains(serviceId) ||
                !ReferenceEquals(slot.Active, oldGeneration) ||
                slot.Starting is not null ||
                slot.Startup is not null ||
                slot.GraphPreparation ||
                oldGeneration.Configuration.Version != configured.Version ||
                !string.Equals(oldGeneration.OwnerExtensionId, GetServiceOwner(serviceId), StringComparison.Ordinal) ||
                !oldGeneration.DependencyBindings.Keys.ToImmutableHashSet().SetEquals(expectedDependencies) ||
                oldGeneration.Lease is not { } lease ||
                !TryResolveLaunchInputs(configured, lease.Port, bindings, out var launchInputs) ||
                !LaunchInputsEqual(oldGeneration, launchInputs))
            {
                return false;
            }
        }

        return true;
    }

    private static HostServiceEndpointLease CreateEndpointLease(ServiceGeneration generation)
    {
        var lease = generation.Lease ?? throw new InvalidOperationException("A ready graph candidate has no port lease.");
        return new HostServiceEndpointLease(
            generation.Configuration.Id,
            generation.GenerationId,
            lease.Port,
            lease.ExpiresAt,
            generation.OwnerExtensionId);
    }

    private static HostServiceRuntimeSnapshot CreateGraphRuntimeSnapshot(
        ServiceGeneration generation,
        long configurationVersion,
        HostServiceRuntimeSnapshot? previous)
    {
        var state = generation.Supervisor.Snapshot;
        var hasProcess = generation.Supervisor.TryGetActiveProcessTelemetry(
            out var processInstanceId,
            out var processId,
            out var startedAt);
        var observation = state.LastHealthObservation;
        var lastHealthAt = observation?.ObservedAt;
        var lastProbe = observation is null
            ? previous?.LastProbe
            : new ExtensionServiceProbeSnapshot(
                observation.ObservedAt,
                MapProbeResult(observation.Status),
                observation.Target,
                MapProbeFailure(observation.Status, generation.LastHealthProbeReason),
                observation.ErrorMessage is { Length: > 256 } error
                    ? error[..256]
                    : observation.ErrorMessage);
        var lastUpdatedAt = state.ChangedAt;
        if (startedAt is { } processStartedAt && processStartedAt > lastUpdatedAt)
        {
            lastUpdatedAt = processStartedAt;
        }

        if (lastHealthAt is { } healthAt && healthAt > lastUpdatedAt)
        {
            lastUpdatedAt = healthAt;
        }

        return new HostServiceRuntimeSnapshot(
            generation.Configuration.Id,
            configurationVersion,
            hasProcess ? processId : null,
            hasProcess ? processInstanceId : null,
            hasProcess ? startedAt : null,
            lastUpdatedAt,
            lastHealthAt,
            ExtensionServiceLifecycleState.Running,
            MapHealth(state.Health),
            generation.OwnerExtensionId,
            lastProbe: lastProbe,
            restartCount: previous?.RestartCount ?? 0,
            stateEnteredAt: state.ChangedAt,
            generationId: generation.GenerationId);
    }

    private async Task RollbackGraphCandidatesAsync(
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates)
    {
        foreach (var serviceId in plan.PreparationOrder.Reverse())
        {
            var slot = _slots[serviceId];
            if (candidates.TryGetValue(serviceId, out var candidate))
            {
                try
                {
                    await DrainAndStopGenerationAsync(slot, candidate).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(RollbackGraphCandidatesAsync));
                }
            }

            lock (slot.Gate)
            {
                if (candidates.TryGetValue(serviceId, out var candidateToClear) &&
                    ReferenceEquals(slot.Starting, candidateToClear))
                {
                    slot.Starting = null;
                }

                slot.GraphPreparation = false;
            }

            if (candidates.TryGetValue(serviceId, out var completedCandidate))
            {
                completedCandidate.GraphPreparation = false;
            }
        }

        SynchronizePublishedRuntimeConfiguration();
        await PublishReadyEndpointsAsync().ConfigureAwait(false);
    }

    private async Task RetireOldGraphAsync(
        ActiveGraphPlan plan,
        Dictionary<Guid, ServiceGeneration> candidates)
    {
        foreach (var serviceId in plan.PreparationOrder.Reverse())
        {
            if (!candidates.ContainsKey(serviceId) ||
                !plan.OldGenerations.TryGetValue(serviceId, out var generation))
            {
                continue;
            }

            var slot = _slots[serviceId];
            try
            {
                await DrainAndStopGenerationAsync(slot, generation).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(RetireOldGraphAsync));
            }
            finally
            {
                RemoveRetiringGeneration(generation);
            }

            PublishServiceState(serviceId, generation.SnapshotVersion, "stopped");
        }
    }
    private bool HasGraphPreparation()
    {
        foreach (var slot in _slots.Values)
        {
            lock (slot.Gate)
            {
                if (slot.GraphPreparation)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private HostServiceCommittedGraphView BuildLifecycleCommittedGraph(
        HostConfigurationSnapshot? snapshot,
        HostServiceCommittedGraphView previous,
        out List<(HostServiceRuntimeSnapshot Snapshot, long ServiceVersion)> runtimePublications)
    {
        var services = ImmutableDictionary.CreateBuilder<Guid, HostServiceCommittedService>();
        var endpoints = ImmutableDictionary.CreateBuilder<Guid, HostServiceEndpointLease>();
        var runtimeSnapshots = previous.RuntimeSnapshots.ToBuilder();
        runtimePublications = [];
        var now = DateTimeOffset.UtcNow;
        lock (_lifecycleGate)
        {
            foreach (var (serviceId, slot) in _slots)
            {
                ServiceGeneration? generation;
                lock (slot.Gate)
                {
                    generation = slot.Active;
                }

                if (generation is not { Ready: true } ||
                    generation.Lease is not { } lease ||
                    !IsValidDependencyLease(lease, serviceId, generation.GenerationId, now))
                {
                    continue;
                }

                var endpoint = CreateEndpointLease(generation);
                var hasRuntime = runtimeSnapshots.TryGetValue(serviceId, out var priorRuntime) &&
                    priorRuntime.GenerationId == generation.GenerationId;
                var retainRuntime = hasRuntime &&
                    (snapshot is null || priorRuntime!.ConfigurationVersion == snapshot.Version);
                var runtime = retainRuntime
                    ? priorRuntime!
                    : CreateGraphRuntimeSnapshot(
                        generation,
                        snapshot?.Version ?? previous.ConfigurationVersion,
                        priorRuntime);
                runtimeSnapshots[serviceId] = runtime;
                if (!retainRuntime)
                {
                    runtimePublications.Add((runtime, generation.Configuration.Version));
                }

                var previousService = previous.Services.TryGetValue(serviceId, out var committedService)
                    ? committedService
                    : null;
                services[serviceId] = CreateCommittedService(generation, endpoint, runtime, previousService);
                endpoints[serviceId] = endpoint;
            }
        }

        var retained = services.Keys.ToHashSet();
        PruneCommittedDependencyClosure(services, retained);

        foreach (var serviceId in services.Keys.Where(serviceId => !retained.Contains(serviceId)).ToArray())
        {
            services.Remove(serviceId);
            endpoints.Remove(serviceId);
        }

        return new HostServiceCommittedGraphView(
            snapshot?.Version ?? previous.ConfigurationVersion,
            services.ToImmutable(),
            endpoints.ToImmutable(),
            runtimeSnapshots.ToImmutable());
    }

    private static void PruneCommittedDependencyClosure(
        ImmutableDictionary<Guid, HostServiceCommittedService>.Builder services,
        HashSet<Guid> retained)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (serviceId, service) in services)
            {
                if (!retained.Contains(serviceId))
                {
                    continue;
                }

                if (service.DependencyBindings.Values.Any(binding =>
                        !retained.Contains(binding.ServiceId) ||
                        !services.TryGetValue(binding.ServiceId, out var dependency) ||
                        dependency.ServiceVersion != binding.ServiceVersion ||
                        dependency.GenerationId != binding.GenerationId ||
                        dependency.Endpoint.Port != binding.Port ||
                        !ReferenceEquals(dependency.ResolvedEnvironment, binding.ResolvedEnvironment) ||
                        !string.Equals(
                            dependency.Endpoint.OwnerExtensionId,
                            binding.OwnerExtensionId,
                            StringComparison.Ordinal)))
                {
                    changed |= retained.Remove(serviceId);
                }
            }
        }
    }

    private HostServiceCommittedService CreateCommittedService(
        ServiceGeneration generation,
        HostServiceEndpointLease endpoint,
        HostServiceRuntimeSnapshot runtime,
        HostServiceCommittedService? previousService)
    {
        var dependencyBindings = previousService is { } committedService &&
            committedService.GenerationId == generation.GenerationId &&
            TryPreserveCommittedDependencyBindings(generation, committedService.DependencyBindings)
                ? committedService.DependencyBindings
                : generation.DependencyBindings.Values.ToImmutableDictionary(
                    static binding => binding.ServiceId,
                    binding => new HostServiceCommittedDependencyBinding(
                        binding.ServiceId,
                        binding.ServiceVersion,
                        binding.GenerationId,
                        binding.LeaseIdentity.Port,
                        binding.ResolvedEnvironment,
                        binding.OwnerExtensionId));

        return new HostServiceCommittedService(
            generation.Configuration.Version,
            generation.GenerationId,
            endpoint,
            dependencyBindings,
            generation.ResolvedEnvironment,
            runtime);
    }
}
