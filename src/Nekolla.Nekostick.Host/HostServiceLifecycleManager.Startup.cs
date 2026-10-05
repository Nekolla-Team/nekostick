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
        lock (slot.Gate)
        {
            slot.Startup = startup.Task;
        }

        var startupDependencyChain = dependencyChain.Add(service.Id);
        ObserveBackgroundTask(
            CompleteStartOrSwitchAsync(
                slot,
                snapshot,
                service,
                startupDependencyChain,
                stopReplacedGeneration,
                startup),
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
        TaskCompletionSource<HostServiceReadinessResult> startup)
    {
        try
        {
            var result = await RunStartOrSwitchAsync(
                slot,
                snapshot,
                service,
                dependencyChain,
                stopReplacedGeneration).ConfigureAwait(false);
            startup.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            HostLogMessages.LifecycleBackgroundCancelled(
                _logger,
                nameof(CompleteStartOrSwitchAsync),
                service.Id);
            startup.TrySetException(exception);
        }
        catch (Exception exception)
        {
            HostLogMessages.LifecycleBackgroundFailed(
                _logger,
                exception,
                nameof(CompleteStartOrSwitchAsync),
                service.Id);
            startup.TrySetException(exception);
        }
        finally
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

            if (!_runtimeState.NewServicesAllowed)
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
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.DatabaseUnavailable);
            }
            PublishConfiguredRuntimeTransition(slot, snapshot, service, ExtensionServiceLifecycleState.Starting);
            await Task.Yield();
            var dependencies = ServiceLaunchTemplate.ExtractDependencies(
                service.ArgumentList.Cast<string?>().Concat(service.Environment.Values));
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

                if (_runtimeEnvironments.ContainsKey(dependencyId))
                {
                    continue;
                }

                var dependency = snapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
                if (dependency is null ||
                    !dependency.Enabled ||
                    !IsServiceEnabledForSnapshot(snapshot, dependencyId))
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
                        snapshot,
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
                    return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
                }
            }


            var candidate = await StartGenerationAsync(
                slot,
                snapshot,
                service,
                attemptNumber,
                _shutdownCts.Token).ConfigureAwait(false);
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

                return _runtimeState.NewServicesAllowed
                    ? new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable)
                    : new(service.Id, snapshot.Version, HostServiceReadinessStatus.DatabaseUnavailable);
            }

            ServiceGeneration? old = null;
            var accepted = false;
            lock (_lifecycleGate)
            {
                var latest = _snapshotHolder.Current;
                var stillConfigured = latest is null || latest.Version < snapshot.Version ||
                    latest.Services.Any(value =>
                        value.Id == service.Id &&
                        value.Version == service.Version &&
                        value.Enabled) &&
                    IsServiceEnabledForSnapshot(latest, service.Id);
                if (!IsStopping && stillConfigured)
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

                    _runtimeEnvironments[service.Id] =
                        new ServiceRuntimeEnvironmentEntry(candidate, candidate.ResolvedEnvironment);
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
                PublishActiveRuntimeState(slot, _snapshotHolder.Current ?? snapshot);
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
                    candidate.Supervisor.Snapshot);
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

    private async Task<ServiceGeneration?> StartGenerationAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        var leaseGate = LeaseLifecycleGate(service.Id);
        await leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var pendingFailure = await RetryPendingLeaseReleasesAsync(
                service.Id,
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

                return null;
            }

            return await StartGenerationWithLeaseGateAsync(
                slot,
                snapshot,
                service,
                attemptNumber,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaseGate.Release();
        }
    }
    private async Task<ServiceGeneration?> StartGenerationWithLeaseGateAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        int attemptNumber,
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
                return null;
            }

            var request = PortLeaseRequest.Automatic(
                _nodeId,
                service.Id,
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
                return null;
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
                return null;
            }

            if (IsStopping ||
                acquired.NodeId != request.NodeId ||
                acquired.ServiceId != request.ServiceId ||
                acquired.Port < rangeStart ||
                acquired.Port > rangeEnd ||
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

                return null;
            }

            Task<SupervisorOperationResult>? startTask = null;
            var resolvedEnvironment = ImmutableDictionary<string, string>.Empty;
            var gateSnapshot = _snapshotHolder.Current is { } latestSnapshot &&
                latestSnapshot.Version > snapshot.Version
                    ? latestSnapshot
                    : snapshot;
            lock (_lifecycleGate)
            {
                if (!IsStopping &&
                    gateSnapshot.Services.Any(value =>
                        value.Id == service.Id &&
                        value.Version == service.Version &&
                        value.Enabled) &&
                    IsServiceEnabledForSnapshot(gateSnapshot, service.Id))
                {
                    try
                    {
                        var created = CreateSupervisor(service, acquired.Port, attemptNumber, acquired, now);
                        supervisor = created.Supervisor;
                        resolvedEnvironment = created.ResolvedEnvironment;
                        var ownerExtensionId = _snapshotHolder.RoutingSnapshot?.ServiceOwners.TryGetValue(
                            service.Id,
                            out var owner)
                            == true
                            ? owner
                            : null;
                        candidate = new ServiceGeneration(
                            service,
                            supervisor,
                            acquired,
                            snapshot.Version,
                            HealthRetryState.Start(service.Id, now, HealthPolicy.StartupTimeout),
                            ownerExtensionId,
                            resolvedEnvironment,
                            ready: false);
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
                return null;
            }

            if (candidate is not null)
            {
                PublishConfiguredRuntimeTransition(slot, snapshot, service, ExtensionServiceLifecycleState.Starting);
                try
                {
                    startTask = candidate.Supervisor.StartAsync(now, _shutdownCts.Token).AsTask();
                }
                catch (Exception exception)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(StartGenerationAsync));
                }
            }

            if (startTask is null)
            {
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
                        ExtensionServiceFailureCode.InvalidLaunchSpecification);
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
                        ExtensionServiceFailureCode.InvalidLaunchSpecification,
                        updatedAt: DateTimeOffset.UtcNow);
                }

                return null;
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

                return null;
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
                return null;
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
                return candidate;
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
                return null;
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
                return null;
            }

            var healthy = await WaitForHealthyAsync(slot, snapshot, candidate, cancellationToken).ConfigureAwait(false);
            if (healthy is not { } ready)
            {
                if (IsStopping || cancellationToken.IsCancellationRequested)
                {
                    await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return null;
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
                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                failedState = supervisor.Snapshot;
                lock (slot.Gate)
                {
                    processExited = candidate.ProcessExitRecorded;
                    processExitCode = candidate.ProcessExitCode;
                }

                failureCode = processExited
                    ? ExtensionServiceFailureCode.ProcessExited
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
                return null;
            }

            candidate.Lease = ready.Lease;
            candidate.HealthRetryState = ready.Retry;
            candidate.Ready = true;
            keepStartingCandidate = true;
            return candidate;
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
        var retry = HealthRetryState.Start(
            generation.Configuration.Id,
            DateTimeOffset.UtcNow,
            HealthPolicy.StartupTimeout);
        while (!IsStopping)
        {
            var observationAt = DateTimeOffset.UtcNow;
            var health = await supervisor.ObserveHealthAsync(retry, observationAt, cancellationToken).ConfigureAwait(false);
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

            if (decision?.Action == HealthRetryAction.Healthy &&
                supervisor.Lease is { } readyLease &&
                !readyLease.IsExpired(observationAt))
            {
                return (readyLease, decision.NextState);
            }

            if (decision is null || decision.Action is HealthRetryAction.Cancelled or HealthRetryAction.Failed or HealthRetryAction.TimedOut)
            {
                return null;
            }

            retry = decision.NextState;
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
