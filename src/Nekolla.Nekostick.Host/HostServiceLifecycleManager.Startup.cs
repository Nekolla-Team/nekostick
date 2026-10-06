using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;
using ContractHealthKind = Nekolla.Nekostick.Contracts.ServiceHealthCheckType;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using ContractStartMode = Nekolla.Nekostick.Contracts.ServiceStartMode;
using DomainHealthKind = Nekolla.Nekostick.Domain.ServiceHealthCheckKind;
using DomainRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostServiceLifecycleManager
{
    /// <summary>Ignores process-exit notifications that do not identify a process generation.</summary>
    public void NotifyProcessExit(Guid serviceId, bool successfulExit)
    {
    }

    /// <summary>Completes a legacy service-only process-exit handoff without mutating lifecycle state.</summary>
    internal Task NotifyProcessExitAsync(Guid serviceId, bool successfulExit)
    {
        _ = this;
        return Task.CompletedTask;
    }

    /// <summary>Records a process-exit observation for the identified process generation.</summary>
    public void NotifyProcessExit(Guid serviceId, ProcessInstanceId instanceId, bool successfulExit) =>
        NotifyProcessExit(serviceId, instanceId, successfulExit, exitCode: null);

    /// <summary>Records an identity-aware process exit with its internal exit code.</summary>
    internal void NotifyProcessExit(
        Guid serviceId,
        ProcessInstanceId instanceId,
        bool successfulExit,
        int? exitCode) =>
        ObserveBackgroundTask(
            NotifyProcessExitAsync(serviceId, instanceId, successfulExit, exitCode),
            nameof(NotifyProcessExitAsync),
            serviceId);

    /// <summary>Completes the identity-aware process-exit handoff.</summary>
    internal Task NotifyProcessExitAsync(
        Guid serviceId,
        ProcessInstanceId instanceId,
        bool successfulExit,
        int? exitCode = null) =>
        NotifyProcessExitAsync(serviceId, instanceId, successfulExit, DateTimeOffset.UtcNow, exitCode);

    private Task NotifyProcessExitAsync(
        Guid serviceId,
        ProcessInstanceId instanceId,
        bool successfulExit,
        DateTimeOffset exitedAt,
        int? exitCode) =>
        HandleProcessExitAsync(serviceId, instanceId, successfulExit, exitedAt, exitCode);

    private void HandleProcessExitObservation(ProcessExitObservation observation) =>
        ObserveBackgroundTask(
            NotifyProcessExitAsync(
                observation.ServiceId,
                observation.InstanceId,
                observation.SuccessfulExit,
                observation.ExitedAt,
                observation.ExitCode),
            nameof(HandleProcessExitAsync),
            observation.ServiceId);

    private async void ObserveBackgroundTask(Task task, string operation, Guid serviceId)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            HostLogMessages.LifecycleBackgroundCancelled(_logger, operation, serviceId);
        }
        catch (Exception exception)
        {
            HostLogMessages.LifecycleBackgroundFailed(_logger, exception, operation, serviceId);
        }
    }

    private Task<HostServiceReadinessResult> StartOrSwitchAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ImmutableHashSet<Guid> dependencyChain,
        bool stopReplacedGeneration = true)
    {
        var startup = new TaskCompletionSource<HostServiceReadinessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long operationId;
        lock (slot.Gate)
        {
            slot.Startup = startup.Task;
            if (slot.StartupOperationId < long.MaxValue)
            {
                slot.StartupOperationId++;
            }

            operationId = slot.StartupOperationId;
        }
        var startupDependencyChain = dependencyChain.Add(service.Id);
        ObserveBackgroundTask(
            CompleteStartOrSwitchAsync(
                slot,
                snapshot,
                service,
                startupDependencyChain,
                stopReplacedGeneration,
                startup,
                operationId),
            nameof(CompleteStartOrSwitchAsync),
            service.Id);
        return startup.Task;
    }

    private async Task CompleteStartOrSwitchAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ImmutableHashSet<Guid> dependencyChain,
        bool stopReplacedGeneration,
        TaskCompletionSource<HostServiceReadinessResult> startup,
        long operationId)
    {
        HostServiceReadinessResult? result = null;
        Exception? completionException = null;
        try
        {
            result = await RunStartOrSwitchAsync(
                slot,
                snapshot,
                service,
                dependencyChain,
                stopReplacedGeneration).ConfigureAwait(false);
            TrackEagerStartupResult(slot, snapshot, service, result, operationId);
        }
        catch (OperationCanceledException exception)
        {
            completionException = exception;
            HostLogMessages.LifecycleBackgroundCancelled(
                _logger,
                nameof(CompleteStartOrSwitchAsync),
                service.Id);
        }
        catch (Exception exception)
        {
            completionException = exception;
            HostLogMessages.LifecycleBackgroundFailed(
                _logger,
                exception,
                nameof(CompleteStartOrSwitchAsync),
                service.Id);
        }
        finally
        {
            try
            {
                lock (slot.Gate)
                {
                    if (ReferenceEquals(slot.Startup, startup.Task))
                    {
                        slot.Startup = null;
                    }
                }
                SynchronizePublishedRuntimeConfiguration();
            }
            finally
            {
                if (completionException is null)
                {
                    startup.TrySetResult(result!);
                }
                else
                {
                    startup.TrySetException(completionException);
                }
            }
        }
    }


    private async Task<HostServiceReadinessResult> RunStartOrSwitchAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ImmutableHashSet<Guid> dependencyChain,
        bool stopReplacedGeneration)
    {
        try
        {
            if (IsStopping)
            {
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }
            var attemptNumber = slot.ReserveStartAttemptNumber();

            var startupGate = _runtimeState.ObserveNewServiceGate();
            if (!startupGate.NewServicesAllowed)
            {
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    service,
                    null,
                    null,
                    ExtensionServiceLifecycleState.Waiting,
                    ExtensionServiceFailureStage.Spawn,
                    ExtensionServiceFailureCode.RuntimeUnavailable);
                return new(
                    service.Id,
                    snapshot.Version,
                    HostServiceReadinessStatus.DatabaseUnavailable,
                    databaseUnavailableProvenance: startupGate.DatabaseUnavailableGateObserved);
            }
            PublishConfiguredRuntimeTransition(slot, snapshot, service, ExtensionServiceLifecycleState.Starting);
            await Task.Yield();
            var dependencies = ServiceLaunchTemplate.ExtractDependencies(
                service.ArgumentList.Cast<string?>().Concat(service.Environment.Values));
            var dependencyBindings = ImmutableDictionary.CreateBuilder<Guid, ServiceDependencyBinding>();
            foreach (var dependencyId in dependencies)
            {
                if (dependencyId == service.Id || dependencyChain.Contains(dependencyId))
                {
                    HostLogMessages.ServiceDependencyUnsatisfied(
                        _logger,
                        service.Id,
                        snapshot.Version,
                        dependencyId);
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.DependencyUnavailable);
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                    return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
                }


                var dependencySnapshot = LatestSnapshot(snapshot);
                var dependency = dependencySnapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
                if (dependency is null ||
                    !dependency.Enabled ||
                    !IsServiceEnabledForSnapshot(dependencySnapshot, dependencyId))
                {
                    HostLogMessages.ServiceDependencyUnsatisfied(
                        _logger,
                        service.Id,
                        snapshot.Version,
                        dependencyId);
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.DependencyUnavailable);
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                    return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
                }

                // Write the wait edge before checking: the edge closing a cycle is always the
                // one being added, so the later writer's walk observes the earlier writer's
                // edge and at least one participant in any concurrent cycle fails fast.
                _startupDependencyWaits[service.Id] = dependencyId;
                HostServiceReadinessResult dependencyResult;
                try
                {
                    if (WouldCreateStartupDependencyCycle(service.Id, dependencyId))
                    {
                        HostLogMessages.ServiceDependencyUnsatisfied(
                            _logger,
                            service.Id,
                            snapshot.Version,
                            dependencyId);
                        PublishRuntimeFailure(
                            slot,
                            snapshot,
                            service,
                            null,
                            null,
                            ExtensionServiceLifecycleState.Waiting,
                            ExtensionServiceFailureStage.Spawn,
                            ExtensionServiceFailureCode.DependencyUnavailable);
                        PublishServiceState(service.Id, snapshot.Version, "unavailable");
                        return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
                    }

                    dependencyResult = await EnsureReadyAsync(
                        dependencySnapshot,
                        dependencyId,
                        dependencyChain.Add(service.Id),
                        _shutdownCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _startupDependencyWaits.TryRemove(service.Id, out _);
                }
                if (dependencyResult.Status != HostServiceReadinessStatus.Ready)
                {
                    HostLogMessages.ServiceDependencyUnsatisfied(
                        _logger,
                        service.Id,
                        snapshot.Version,
                        dependencyId);
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.DependencyUnavailable);
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                    return new(
                        service.Id,
                        snapshot.Version,
                        HostServiceReadinessStatus.Unavailable,
                        databaseUnavailableProvenance:
                            dependencyResult.DatabaseUnavailableProvenance);
                }
                if (!TryCaptureDependencyBinding(
                        dependencySnapshot,
                        dependencyId,
                        dependency.Version,
                        out var dependencyBinding) ||
                    dependencyBinding is null)
                {
                    HostLogMessages.ServiceDependencyUnsatisfied(
                        _logger,
                        service.Id,
                        snapshot.Version,
                        dependencyId);
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.DependencyUnavailable);
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                    return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
                }

                dependencyBindings.Add(dependencyId, dependencyBinding);
            }
            var capturedDependencyBindings = dependencyBindings.ToImmutable();
            var generationResult = await StartGenerationAsync(
                slot,
                snapshot,
                service,
                attemptNumber,
                capturedDependencyBindings,
                _shutdownCts.Token).ConfigureAwait(false);
            var candidate = generationResult.Generation;
            if (candidate is null)
            {
                if (!IsStopping)
                {
                    var latest = _snapshotHolder.Current;
                    var stillConfigured = latest is null || latest.Version < snapshot.Version ||
                        latest.Services.Any(value =>
                            value.Id == service.Id &&
                            value.Version == service.Version &&
                            value.Enabled) &&
                        IsServiceEnabledForSnapshot(latest, service.Id);
                    if (!stillConfigured)
                    {
                        SynchronizePublishedRuntimeConfiguration(latest);
                        PublishActiveRuntimeState(slot, latest ?? snapshot);
                        return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
                    }

                    if (!_runtimeRegistry.TryGet(service.Id, out var current) ||
                        current.FailureCode == ExtensionServiceFailureCode.None &&
                        current.FailureStage == ExtensionServiceFailureStage.None)
                    {
                        PublishRuntimeFailure(
                            slot,
                            snapshot,
                            service,
                            null,
                            null,
                            ExtensionServiceLifecycleState.Failed,
                            ExtensionServiceFailureStage.Spawn,
                            ExtensionServiceFailureCode.StartRejected);
                    }

                    HostLogMessages.ServiceLaunchRejected(_logger, service.Id, snapshot.Version);
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                }

                if (IsStopping)
                {
                    return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
                }

                var unavailableStatus = _runtimeState.NewServicesAllowed
                    ? HostServiceReadinessStatus.Unavailable
                    : HostServiceReadinessStatus.DatabaseUnavailable;
                return new(
                    service.Id,
                    snapshot.Version,
                    unavailableStatus,
                    databaseUnavailableProvenance:
                        generationResult.DatabaseUnavailableProvenance);
            }

            ServiceGeneration? old = null;
            var accepted = false;
            var candidateServiceStillConfigured = false;
            var dependencyBindingsCurrent = true;
            var unavailableDependencyId = Guid.Empty;
            lock (_lifecycleGate)
            {
                var latest = LatestSnapshot(snapshot);
                var currentService = latest.Services.FirstOrDefault(value => value.Id == service.Id);
                candidateServiceStillConfigured = currentService is { Enabled: true } &&
                    currentService.Version == service.Version &&
                    IsServiceEnabledForSnapshot(latest, service.Id);
                dependencyBindingsCurrent = AreDependencyBindingsCurrent(
                    latest,
                    capturedDependencyBindings,
                    out unavailableDependencyId);
                if (!IsStopping && candidateServiceStillConfigured && dependencyBindingsCurrent)
                {
                    lock (slot.Gate)
                    {
                        old = slot.Active;
                        slot.Active = candidate;
                        if (ReferenceEquals(slot.Starting, candidate))
                        {
                            slot.Starting = null;
                        }
                    }

                    accepted = true;
                }
            }

            if (!accepted)
            {
                await StopGenerationAsync(slot, candidate, CancellationToken.None).ConfigureAwait(false);
                lock (slot.Gate)
                {
                    if (ReferenceEquals(slot.Starting, candidate))
                    {
                        slot.Starting = null;
                    }
                }

                SynchronizePublishedRuntimeConfiguration();
                PublishActiveRuntimeState(slot, LatestSnapshot(snapshot));
                if (!IsStopping && candidateServiceStillConfigured && !dependencyBindingsCurrent)
                {
                    HostLogMessages.ServiceDependencyUnsatisfied(
                        _logger,
                        service.Id,
                        snapshot.Version,
                        unavailableDependencyId);
                    var failureSnapshot = LatestSnapshot(snapshot);
                    var failureService = failureSnapshot.Services.FirstOrDefault(value => value.Id == service.Id) ?? service;
                    PublishRuntimeFailure(
                        slot,
                        failureSnapshot,
                        failureService,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.DependencyUnavailable);
                    PublishServiceState(service.Id, failureSnapshot.Version, "unavailable");
                    return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
                }

                PublishServiceState(service.Id, snapshot.Version, "stopped");
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            if (!candidate.Ready)
            {
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
                PublishRuntimeSnapshot(candidate, lifecycleState: ExtensionServiceLifecycleState.Waiting);
                PublishServiceState(service.Id, candidate.SnapshotVersion, "waiting");
                if (old is not null && !ReferenceEquals(old, candidate) && stopReplacedGeneration)
                {
                    await DrainAndStopGenerationAsync(slot, old).ConfigureAwait(false);
                    PublishServiceState(old.Configuration.Id, old.SnapshotVersion, "stopped");
                }

                return new(
                    service.Id,
                    snapshot.Version,
                    HostServiceReadinessStatus.Unavailable,
                    candidate.Supervisor.Snapshot,
                    databaseUnavailableProvenance:
                        generationResult.DatabaseUnavailableProvenance);
            }

            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            HostLogMessages.ServiceReady(_logger, service.Id, candidate.SnapshotVersion);
            PublishRuntimeSnapshot(candidate, lifecycleState: ExtensionServiceLifecycleState.Running);
            PublishServiceState(service.Id, candidate.SnapshotVersion, "ready");
            if (old is not null && !ReferenceEquals(old, candidate) && stopReplacedGeneration)
            {
                await DrainAndStopGenerationAsync(slot, old).ConfigureAwait(false);
                PublishServiceState(old.Configuration.Id, old.SnapshotVersion, "stopped");
            }

            if (IsStopping)
            {
                await StopGenerationAsync(slot, candidate, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(service.Id, candidate.SnapshotVersion, "stopped");
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Ready, candidate.Supervisor.Snapshot);
        }
        catch (OperationCanceledException)
        {
            HostLogMessages.LifecycleBackgroundCancelled(_logger, nameof(StartOrSwitchAsync), service.Id);
            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(StartOrSwitchAsync));
            HostLogMessages.ServiceLaunchRejected(_logger, service.Id, snapshot.Version);
            PublishRuntimeFailure(
                slot,
                snapshot,
                service,
                null,
                null,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                ExtensionServiceFailureCode.Unknown);
            PublishServiceState(service.Id, snapshot.Version, "unavailable");
            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
        }
    }

    private async Task<(ServiceGeneration? Generation, bool DatabaseUnavailableProvenance)> StartGenerationAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        int attemptNumber,
        ImmutableDictionary<Guid, ServiceDependencyBinding> dependencyBindings,
        CancellationToken cancellationToken,
        bool graphPreparation = false)
    {
        var generationId = Guid.CreateVersion7();
        var leaseGate = LeaseLifecycleGate(service.Id);
        await leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var pendingFailure = await RetryPendingLeaseReleasesAsync(
                service.Id,
                generationId,
                now,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingFailure is { } status)
            {
                if (status == PortLeaseOperationStatus.DatabaseUnavailable)
                {
                    _runtimeState.MarkDatabaseUnavailable();
                }

                if (!IsStopping)
                {
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        status == PortLeaseOperationStatus.DatabaseUnavailable
                            ? ExtensionServiceFailureCode.RuntimeUnavailable
                            : ExtensionServiceFailureCode.PortLeaseUnavailable);
                }

                return (null, status == PortLeaseOperationStatus.DatabaseUnavailable);
            }

            return await StartGenerationWithLeaseGateAsync(
                slot,
                snapshot,
                service,
                generationId,
                attemptNumber,
                dependencyBindings,
                graphPreparation,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaseGate.Release();
        }
    }
    private async Task<(ServiceGeneration? Generation, bool DatabaseUnavailableProvenance)> StartGenerationWithLeaseGateAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        Guid generationId,
        int attemptNumber,
        ImmutableDictionary<Guid, ServiceDependencyBinding> dependencyBindings,
        bool graphPreparation,
        CancellationToken cancellationToken)
    {
        ServiceGeneration? candidate = null;
        ServiceSupervisor? supervisor = null;
        var keepStartingCandidate = false;
        try
        {
            var rangeStart = snapshot.GlobalSettings.AutoPortRangeStart;
            var rangeEnd = snapshot.GlobalSettings.AutoPortRangeEnd;
            if (rangeStart < 1 || rangeEnd > 65535 || rangeStart > rangeEnd)
            {
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    service,
                    null,
                    null,
                    ExtensionServiceLifecycleState.Failed,
                    ExtensionServiceFailureStage.Spawn,
                    ExtensionServiceFailureCode.InvalidLaunchSpecification);
                return (null, false);
            }

            var request = PortLeaseRequest.Automatic(
                _nodeId,
                service.Id,
                generationId,
                LeasePolicy.TimeToLive,
                rangeStart,
                rangeEnd);
            var now = DateTimeOffset.UtcNow;
            PortLeaseOperationResult leaseResult;
            try
            {
                leaseResult = await _leaseStore.ApplyAsync(
                    PortLeaseIntent.Acquire(request),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(StartGenerationAsync));
                _runtimeState.MarkDatabaseUnavailable();
                if (!cancellationToken.IsCancellationRequested)
                {
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.RuntimeUnavailable);

                }
                return (null, true);
            }

            var acquired = leaseResult.Lease;
            if (leaseResult.Status != PortLeaseOperationStatus.Applied || acquired is null)
            {
                if (leaseResult.Status == PortLeaseOperationStatus.DatabaseUnavailable)
                {
                    _runtimeState.MarkDatabaseUnavailable();
                }

                if (leaseResult.Status != PortLeaseOperationStatus.Cancelled || !cancellationToken.IsCancellationRequested)
                {
                    var failureCode = leaseResult.Status == PortLeaseOperationStatus.DatabaseUnavailable
                        ? ExtensionServiceFailureCode.RuntimeUnavailable
                        : ExtensionServiceFailureCode.PortLeaseUnavailable;
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        failureCode);

                }
                return (null, leaseResult.Status == PortLeaseOperationStatus.DatabaseUnavailable);
            }

            if (IsStopping ||
                acquired.NodeId != request.NodeId ||
                acquired.ServiceId != request.ServiceId ||
                acquired.Port < rangeStart ||
                acquired.Port > rangeEnd ||
                acquired.GenerationId != request.GenerationId ||
                acquired.IsExpired(now))
            {
                _ = await ReleaseAutomaticLeaseAsync(request, acquired, CancellationToken.None).ConfigureAwait(false);
                if (!IsStopping)
                {
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        null,
                        null,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.PortLeaseUnavailable);
                }

                return (null, false);
            }

            Task<SupervisorOperationResult>? startTask = null;
            var resolvedArguments = ImmutableArray<string>.Empty;
            var resolvedEnvironment = ImmutableDictionary<string, string>.Empty;
            var dependencyBindingUnavailable = false;
            var candidateSuperseded = false;
            var unavailableDependencyId = Guid.Empty;
            lock (_lifecycleGate)
            {
                var gateSnapshot = LatestSnapshot(snapshot);
                if (!IsStopping &&
                    gateSnapshot.Services.Any(value =>
                        value.Id == service.Id &&
                        value.Version == service.Version &&
                        value.Enabled) &&
                    IsServiceEnabledForSnapshot(gateSnapshot, service.Id))
                {
                    try
                    {
                        Func<Guid, string, string?> remoteEnvironmentResolver = (dependencyId, name) =>
                            dependencyBindings.TryGetValue(dependencyId, out var binding) &&
                            binding.ResolvedEnvironment.TryGetValue(name, out var value)
                                ? value
                                : null;
                        var created = CreateSupervisor(
                            service,
                            generationId,
                            acquired.Port,
                            attemptNumber,
                            remoteEnvironmentResolver,
                            acquired,
                            now);
                        supervisor = created.Supervisor;
                        resolvedArguments = created.ResolvedArguments;
                        resolvedEnvironment = created.ResolvedEnvironment;
                        var ownerExtensionId = GetServiceOwner(service.Id);
                        candidate = new ServiceGeneration(
                            service,
                            generationId,
                            supervisor,
                            acquired,
                            snapshot.Version,
                            HealthRetryState.StartStartup(service.Id, now, HealthPolicy.StartupTimeout),
                            ownerExtensionId,
                            resolvedArguments,
                            resolvedEnvironment,
                            dependencyBindings,
                            ready: false,
                            graphPreparation: graphPreparation);
                        lock (slot.Gate)
                        {
                            slot.Starting = candidate;
                        }
                    }
                    catch (Exception exception)
                    {
                        HostLogMessages.FailureDetails(_logger, exception, nameof(CreateSupervisor));
                        PublishRuntimeFailure(
                            slot,
                            snapshot,
                            service,
                            null,
                            null,
                            ExtensionServiceLifecycleState.Failed,
                            ExtensionServiceFailureStage.Spawn,
                            ExtensionServiceFailureCode.InvalidLaunchSpecification);
                    }
                }
            }

            if (supervisor is null)
            {
                _ = await ReleaseAutomaticLeaseAsync(request, acquired, CancellationToken.None).ConfigureAwait(false);
                return (null, false);
            }

            if (candidate is not null)
            {
                PublishConfiguredRuntimeTransition(slot, snapshot, service, ExtensionServiceLifecycleState.Starting);
                lock (_lifecycleGate)
                {
                    var latest = LatestSnapshot(snapshot);
                    var currentService = latest.Services.FirstOrDefault(value => value.Id == service.Id);
                    var stillConfigured = currentService is { Enabled: true } &&
                        currentService.Version == service.Version &&
                        IsServiceEnabledForSnapshot(latest, service.Id);
                    if (IsStopping || !stillConfigured)
                    {
                        candidateSuperseded = true;
                    }
                    else if (!graphPreparation && !AreDependencyBindingsCurrent(
                        latest,
                        dependencyBindings,
                        out unavailableDependencyId))
                    {
                        dependencyBindingUnavailable = true;
                    }
                    else
                    {
                        try
                        {
                            startTask = candidate.Supervisor.StartAsync(now, cancellationToken).AsTask();
                        }
                        catch (Exception exception)
                        {
                            HostLogMessages.FailureDetails(_logger, exception, nameof(StartGenerationAsync));
                        }
                    }
                }
            }

            if (startTask is null)
            {
                if (candidateSuperseded || IsStopping)
                {
                    await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return (null, false);
                }

                var failureCode = dependencyBindingUnavailable
                    ? ExtensionServiceFailureCode.DependencyUnavailable
                    : ExtensionServiceFailureCode.InvalidLaunchSpecification;
                var failureSnapshot = dependencyBindingUnavailable ? LatestSnapshot(snapshot) : snapshot;
                var failureService = dependencyBindingUnavailable
                    ? failureSnapshot.Services.FirstOrDefault(value => value.Id == service.Id) ?? service
                    : service;
                var failedState = supervisor.Snapshot;
                if (candidate is not null)
                {
                    if (dependencyBindingUnavailable)
                    {
                        HostLogMessages.ServiceDependencyUnsatisfied(
                            _logger,
                            service.Id,
                            snapshot.Version,
                            unavailableDependencyId);
                    }

                    PublishRuntimeFailure(
                        slot,
                        failureSnapshot,
                        failureService,
                        candidate,
                        failedState,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        failureCode);
                }

                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                if (candidate is not null)
                {
                    PublishRuntimeFailure(
                        slot,
                        failureSnapshot,
                        failureService,
                        candidate,
                        failedState,
                        ExtensionServiceLifecycleState.Waiting,
                        ExtensionServiceFailureStage.Spawn,
                        failureCode,
                        updatedAt: DateTimeOffset.UtcNow);
                }

                return (null, false);
            }

            SupervisorOperationResult started;
            try
            {
                started = await startTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(StartGenerationAsync));
                var failedState = supervisor.Snapshot;
                if (candidate is not null)
                {
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        candidate,
                        failedState,
                        ExtensionServiceLifecycleState.Failed,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.Unknown);
                }

                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                if (candidate is not null)
                {
                    PublishRuntimeFailure(
                        slot,
                        snapshot,
                        service,
                        candidate,
                        failedState,
                        ExtensionServiceLifecycleState.Failed,
                        ExtensionServiceFailureStage.Spawn,
                        ExtensionServiceFailureCode.Unknown,
                        updatedAt: DateTimeOffset.UtcNow);
                }

                return (null, false);
            }

            if (started.Reason == ServiceStateReasonCode.MissingHostEnvironment &&
                started.FailureMessage is { } placeholder)
            {
                HostLogMessages.ServiceLaunchMissingHostEnvironment(
                    _logger,
                    service.Id,
                    snapshot.Version,
                    placeholder);
            }

            if (candidate is null)
            {
                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return (null, false);
            }

            if (started.Snapshot.ObservedLifecycle == ServiceLifecycleState.Waiting)
            {
                candidate.Lease = null;
                candidate.Ready = false;
                var failureCode = MapFailureCode(started.Reason);
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    service,
                    candidate,
                    started.Snapshot,
                    ExtensionServiceLifecycleState.Waiting,
                    ExtensionServiceFailureStage.Spawn,
                    failureCode == ExtensionServiceFailureCode.None
                        ? ExtensionServiceFailureCode.Unknown
                        : failureCode,
                    retryAt: started.Snapshot.Deadline?.At);
                keepStartingCandidate = true;
                return (candidate, started.Reason == ServiceStateReasonCode.DatabaseUnavailable);
            }

            if (started.Status != SupervisorOperationStatus.Applied || supervisor.Lease is null)
            {
                if (started.Reason == ServiceStateReasonCode.DatabaseUnavailable)
                {
                    _runtimeState.MarkDatabaseUnavailable();
                }

                var failureCode = MapFailureCode(started.Reason);
                if (failureCode == ExtensionServiceFailureCode.None)
                {
                    failureCode = ExtensionServiceFailureCode.StartRejected;
                }

                var failureLifecycle = failureCode is ExtensionServiceFailureCode.RuntimeUnavailable or
                    ExtensionServiceFailureCode.PortLeaseUnavailable
                    ? ExtensionServiceLifecycleState.Waiting
                    : ExtensionServiceLifecycleState.Failed;
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    service,
                    candidate,
                    started.Snapshot,
                    failureLifecycle,
                    ExtensionServiceFailureStage.Spawn,
                    failureCode);
                return (null, started.Reason == ServiceStateReasonCode.DatabaseUnavailable);
            }
            if (!_serviceLogBufferRegistry.HasOutputTap &&
                started.Status == SupervisorOperationStatus.Applied &&
                candidate.Supervisor.ActiveProcessInstance is { } processInstanceId)
            {
                _serviceLogBufferRegistry.OnGenerationStarted(
                    service.Id,
                    processInstanceId,
                    attemptNumber,
                    started.Snapshot.ChangedAt);
            }

            if (IsStopping)
            {
                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return (null, false);
            }

            var healthy = await WaitForHealthyAsync(slot, snapshot, candidate, cancellationToken).ConfigureAwait(false);
            if (healthy is not { } ready)
            {
                if (IsStopping || cancellationToken.IsCancellationRequested)
                {
                    await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return (null, false);
                }

                var failedState = supervisor.Snapshot;
                bool processExited;
                int? processExitCode;
                lock (slot.Gate)
                {
                    processExited = candidate.ProcessExitRecorded;
                    processExitCode = candidate.ProcessExitCode;
                }

                var failureCode = processExited
                    ? ExtensionServiceFailureCode.ProcessExited
                    : candidate.LastHealthProbeReason == ServiceStateReasonCode.PortLeaseUnavailable
                        ? ExtensionServiceFailureCode.PortLeaseUnavailable
                        : failedState.LastHealthObservation is { } observation
                            ? MapProbeFailure(observation.Status, failedState.Reason)
                            : ExtensionServiceFailureCode.HealthTimeout;
                if (failureCode == ExtensionServiceFailureCode.None)
                {
                    failureCode = ExtensionServiceFailureCode.HealthTimeout;
                }

                var failureStage = processExited
                    ? ExtensionServiceFailureStage.ProcessExit
                    : ExtensionServiceFailureStage.HealthProbe;
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    service,
                    candidate,
                    failedState,
                    ExtensionServiceLifecycleState.Failed,
                    failureStage,
                    failureCode,
                    processExitCode: processExitCode);
                var stopResult = await supervisor.StopAsync(
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
                if (stopResult.Reason == ServiceStateReasonCode.DatabaseUnavailable)
                {
                    _runtimeState.MarkDatabaseUnavailable();
                }
                failedState = supervisor.Snapshot;
                lock (slot.Gate)
                {
                    processExited = candidate.ProcessExitRecorded;
                    processExitCode = candidate.ProcessExitCode;
                }

                failureCode = processExited
                    ? ExtensionServiceFailureCode.ProcessExited
                    : candidate.LastHealthProbeReason == ServiceStateReasonCode.PortLeaseUnavailable
                        ? ExtensionServiceFailureCode.PortLeaseUnavailable
                        : failedState.LastHealthObservation is { } stoppedObservation
                            ? MapProbeFailure(stoppedObservation.Status, failedState.Reason)
                            : ExtensionServiceFailureCode.HealthTimeout;
                if (failureCode == ExtensionServiceFailureCode.None)
                {
                    failureCode = ExtensionServiceFailureCode.HealthTimeout;
                }

                failureStage = processExited
                    ? ExtensionServiceFailureStage.ProcessExit
                    : ExtensionServiceFailureStage.HealthProbe;
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    service,
                    candidate,
                    failedState,
                    ExtensionServiceLifecycleState.Failed,
                    failureStage,
                    failureCode,
                    processExitCode: processExitCode,
                    updatedAt: DateTimeOffset.UtcNow);
                return (null, stopResult.Reason == ServiceStateReasonCode.DatabaseUnavailable);
            }

            candidate.Lease = ready.Lease;
            candidate.HealthRetryState = ready.Retry;
            candidate.Ready = true;
            keepStartingCandidate = true;
            return (candidate, false);
        }
        finally
        {
            RetainPendingLeaseRelease(supervisor?.PendingLeaseRelease);
            if (!keepStartingCandidate && candidate is not null)
            {
                lock (slot.Gate)
                {
                    if (ReferenceEquals(slot.Starting, candidate))
                    {
                        slot.Starting = null;
                    }
                }

                SynchronizePublishedRuntimeConfiguration();
            }
        }
    }

    private async Task<(PortLease Lease, HealthRetryState Retry)?> WaitForHealthyAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceGeneration generation,
        CancellationToken cancellationToken)
    {
        var supervisor = generation.Supervisor;
        var startupStartedAt = DateTimeOffset.UtcNow;
        var retry = HealthRetryState.StartStartup(
            generation.Configuration.Id,
            startupStartedAt,
            HealthPolicy.StartupTimeout);
        generation.HealthRetryState = retry;
        if (IsStopping ||
            !await supervisor.BeginStartupHealthAsync(retry, startupStartedAt, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        while (!IsStopping)
        {
            var observationAt = DateTimeOffset.UtcNow;
            var health = await supervisor.ObserveStartupHealthAsync(retry, observationAt, cancellationToken).ConfigureAwait(false);
            var decision = health.Health;
            var observedState = supervisor.Snapshot;
            generation.LastHealthProbeReason = observedState.Reason;
            PublishCandidateRuntimeObservation(
                slot,
                snapshot,
                generation,
                observedState,
                ExtensionServiceLifecycleState.Starting,
                observedState.Reason);

            if (decision?.Action == HealthRetryAction.Healthy)
            {
                if (supervisor.Lease is { } readyLease &&
                    !readyLease.IsExpired(observationAt))
                {
                    return (readyLease, decision.NextState);
                }

                generation.LastHealthProbeReason = ServiceStateReasonCode.PortLeaseUnavailable;
                return null;
            }
            else
            {
                if (decision is null || decision.Action is HealthRetryAction.Cancelled or HealthRetryAction.Failed or HealthRetryAction.TimedOut)
                {
                    return null;
                }

                retry = decision.NextState;
            }
            if (decision.NextAttemptAt is { } next)
            {
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return null;
    }


    private void PublishServiceState(Guid serviceId, long version, string state)
    {
        HostCoreEventPublisher.Publish(
            _runtimeManager,
            ExtensionCoreEventKind.ServiceStateChanged,
            new
            {
                serviceId,
                version,
                state
            },
            _logger);
    }
}
