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
    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> ResumeAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopping)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.StorageUnavailable,
                $"Service '{serviceId}' cannot be resumed because the host service lifecycle is stopping."));
        }

        var snapshot = _snapshotHolder.Current;
        if (snapshot is null)
        {
            var hasRuntimeSlot = _slots.ContainsKey(serviceId);
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                hasRuntimeSlot ? ConfigurationErrorCode.StorageUnavailable : ConfigurationErrorCode.NotFound,
                hasRuntimeSlot
                    ? $"The current host configuration snapshot is unavailable, so service '{serviceId}' cannot be resumed."
                    : $"Service '{serviceId}' could not be located for resume because no runtime slot exists and the current host configuration snapshot is unavailable."));
        }

        var service = snapshot.Services.FirstOrDefault(value => value.Id == serviceId);
        if (service is null)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.NotFound,
                $"Service '{serviceId}' was not found in the current host configuration."));
        }

        if (!_runtimeState.NewServicesAllowed)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.StorageUnavailable,
                $"Service '{serviceId}' cannot be resumed because the host service-runtime gate disallows new service operations."));
        }

        if (!_slots.TryGetValue(serviceId, out var slot))
        {
            return ConfigurationWriteResult.NoOp();
        }

        Task<HostServiceReadinessResult> retryTask;
        lock (_lifecycleGate)
        {
            lock (slot.Gate)
            {
                if (slot.Active is not { } generation ||
                    generation.Configuration.Version != service.Version ||
                    generation.Supervisor.Snapshot.ObservedLifecycle != ServiceLifecycleState.Waiting)
                {
                    return ConfigurationWriteResult.NoOp();
                }

                if (slot.Startup is { } existing)
                {
                    retryTask = existing;
                }
                else
                {
                    slot.StartupGeneration = service.Version;
                    retryTask = RetryWaitingGenerationAsync(slot, generation, snapshot, cancellationToken);
                    slot.Startup = retryTask;
                }
            }
        }

        try
        {
            var result = await retryTask.ConfigureAwait(false);
            return result.Status switch
            {
                HostServiceReadinessStatus.Ready => ConfigurationWriteResult.Success(),
                HostServiceReadinessStatus.DatabaseUnavailable => ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.StorageUnavailable,
                        $"Service '{serviceId}' could not be resumed because the database gate is unavailable.")),
                HostServiceReadinessStatus.Disabled => ConfigurationWriteResult.NoOp(),
                HostServiceReadinessStatus.Cancelled => ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.StorageUnavailable,
                        $"The host cancelled the readiness retry for service '{serviceId}' before it became ready.")),
                _ => ConfigurationWriteResult.Failure(
                    new ConfigurationError(
                        ConfigurationErrorCode.Validation,
                        $"Service '{serviceId}' did not become ready during resume; the lifecycle manager returned readiness status '{result.Status}'.")),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.Validation,
                $"The readiness retry for service '{serviceId}' failed with {exception.GetType().Name}."));
        }
        finally
        {
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Startup, retryTask))
                {
                    slot.Startup = null;
                }
            }
            SynchronizePublishedRuntimeConfiguration();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> RestartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopping)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.StorageUnavailable,
                $"Service '{serviceId}' cannot be restarted because the host service lifecycle is stopping."));
        }

        var snapshot = _snapshotHolder.Current;
        if (snapshot is null)
        {
            var hasRuntimeSlot = _slots.ContainsKey(serviceId);
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                hasRuntimeSlot ? ConfigurationErrorCode.StorageUnavailable : ConfigurationErrorCode.NotFound,
                hasRuntimeSlot
                    ? $"The current host configuration snapshot is unavailable, so service '{serviceId}' cannot be restarted."
                    : $"Service '{serviceId}' could not be located for restart because no runtime slot exists and the current host configuration snapshot is unavailable."));
        }

        var service = snapshot.Services.FirstOrDefault(value => value.Id == serviceId);
        if (service is null)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.NotFound,
                $"Service '{serviceId}' was not found in the current host configuration."));
        }

        if (!service.Enabled)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.Validation,
                $"Service '{serviceId}' has Enabled=false; restarting requires Enabled=true."));
        }

        if (!IsServiceEnabledForSnapshot(snapshot, serviceId))
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.Validation,
                $"Service '{serviceId}' is disabled by the effective host configuration; restart requires effective enablement, which is currently false."));
        }

        if (!_runtimeState.NewServicesAllowed && !HasPendingLeaseRelease(serviceId))
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.StorageUnavailable,
                $"Service '{serviceId}' cannot be restarted because the host service-runtime gate disallows new service operations."));
        }

        CancelGraphPreparationsForExplicitRestart(snapshot.Version);
        await WaitForGraphTransactionAsync(cancellationToken).ConfigureAwait(false);
        Task<HostServiceReadinessResult>? existingStartup = null;
        if (_slots.TryGetValue(serviceId, out var slot))
        {
            lock (slot.Gate)
            {
                existingStartup = slot.Startup;
            }
        }

        if (existingStartup is not null)
        {
            try
            {
                await existingStartup.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        var withdrawal = await WithdrawAsync(serviceId, CancellationToken.None).ConfigureAwait(false);
        if (!withdrawal.ProcessStopped)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.Validation,
                $"Service '{serviceId}' could not be restarted because its previous process did not stop."));
        }
        if (IsStopping)
        {
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.StorageUnavailable,
                $"Service '{serviceId}' cannot be restarted because the host service lifecycle stopped during endpoint withdrawal."));
        }
        var readiness = await EnsureReadyAsync(
            snapshot,
            serviceId,
            ImmutableHashSet<Guid>.Empty,
            cancellationToken).ConfigureAwait(false);
        if (readiness.Status == HostServiceReadinessStatus.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (readiness.Status != HostServiceReadinessStatus.Ready)
        {
            var code = readiness.Status == HostServiceReadinessStatus.DatabaseUnavailable
                ? ConfigurationErrorCode.StorageUnavailable
                : ConfigurationErrorCode.Validation;
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                code,
                readiness.Status == HostServiceReadinessStatus.DatabaseUnavailable
                    ? $"Service '{serviceId}' could not be restarted because the database gate is unavailable."
                    : $"Service '{serviceId}' could not be restarted because it did not become ready (status '{readiness.Status}')."));
        }
        var graph = await RefreshChangedActiveGraphAsync(snapshot, cancellationToken, forceRetry: true)
            .ConfigureAwait(false);
        if (graph.Required && !graph.Committed)
        {
            var code = graph.DatabaseUnavailable
                ? ConfigurationErrorCode.StorageUnavailable
                : ConfigurationErrorCode.Validation;
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                code,
                graph.DatabaseUnavailable
                    ? $"Service '{serviceId}' restarted, but its active dependents could not be rebound because the database gate is unavailable."
                    : $"Service '{serviceId}' restarted, but its active dependency graph did not become ready."));
        }

        if (withdrawal.LeaseReleaseFailure is { } releaseStatus)
        {
            var code = releaseStatus == PortLeaseOperationStatus.DatabaseUnavailable
                ? ConfigurationErrorCode.StorageUnavailable
                : ConfigurationErrorCode.Validation;
            return ConfigurationWriteResult.Failure(new ConfigurationError(
                code,
                $"Service '{serviceId}' restarted successfully, but its previous generation's port lease release remains unresolved (status '{releaseStatus}')."));
        }

        return ConfigurationWriteResult.Success();
    }

    private async Task HandleTerminalHealthAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        if (IsStopping)
        {
            return;
        }

        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Active, generation) ||
                !_retiringGenerations.TryAdd(generation, new RetiringGenerationState()))
            {
                return;
            }

            generation.Ready = false;
        }
        var failureState = generation.Supervisor.Snapshot;
        generation.LastHealthProbeReason = failureState.Reason;
        var healthFailureCode = failureState.LastHealthObservation is { } observation
            ? MapProbeFailure(observation.Status, failureState.Reason)
            : ExtensionServiceFailureCode.HealthCheckFailed;
        if (healthFailureCode == ExtensionServiceFailureCode.None)
        {
            healthFailureCode = ExtensionServiceFailureCode.HealthCheckFailed;
        }
        PublishRuntimeSnapshot(
            generation,
            failureState,
            ExtensionServiceLifecycleState.Failed,
            ExtensionServiceFailureStage.HealthProbe,
            healthFailureCode,
            preserveServiceVersion: true);
        PublishServiceState(
            generation.Configuration.Id,
            generation.SnapshotVersion,
            "unavailable");

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        if (IsStopping)
        {
            return;
        }

        SupervisorOperationResult result;
        lock (slot.Gate)
        {
            if (IsStopping || !ReferenceEquals(slot.Active, generation))
            {
                return;
            }

            result = generation.Supervisor.RecordProcessExitPreservingInstance(false, DateTimeOffset.UtcNow);
        }

        if (result.Restart is { ShouldRestart: true, NotBefore: { } notBefore } && !IsStopping)
        {
            PublishRuntimeSnapshot(
                generation,
                result.Snapshot,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceFailureStage.HealthProbe,
                healthFailureCode,
                retryAt: notBefore,
                preserveServiceVersion: true);
            HostLogMessages.ServiceRestartScheduled(_logger, generation.Configuration.Id);
            ObserveBackgroundTask(
                RestartTerminalAfterAsync(slot, generation, notBefore, CancellationToken.None),
                nameof(RestartTerminalAfterAsync),
                generation.Configuration.Id);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "restarting");
            return;
        }

        var restartFailureCode = MapFailureCode(result.Reason);
        if (restartFailureCode is not (ExtensionServiceFailureCode.RestartPolicyDisabled or ExtensionServiceFailureCode.RestartLimitReached))
        {
            restartFailureCode = healthFailureCode;
        }
        PublishRuntimeSnapshot(
            generation,
            result.Snapshot,
            ExtensionServiceLifecycleState.Failed,
            ExtensionServiceFailureStage.HealthProbe,
            restartFailureCode,
            preserveServiceVersion: true);
        ObserveBackgroundTask(
            StopRetiringGenerationAsync(slot, generation),
            nameof(StopRetiringGenerationAsync),
            generation.Configuration.Id);
    }

    private async Task RestartAfterAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            await RestartAfterCrashCoreAsync(slot, generation, notBefore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HostLogMessages.LifecycleBackgroundCancelled(
                _logger,
                nameof(RestartAfterAsync),
                generation.Configuration.Id);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(RestartAfterAsync));
        }
    }

    private async Task RestartAfterCrashCoreAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        var delay = notBefore - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (IsStopping || !IsCurrentGeneration(slot, generation))
        {
            return;
        }

        var snapshot = _snapshotHolder.Current;
        var configuredService = snapshot?.Services.FirstOrDefault(value =>
            value.Id == generation.Configuration.Id);
        if (snapshot is null ||
            configuredService is not { Enabled: true } ||
            configuredService.Version != generation.Configuration.Version ||
            !IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id) ||
            !string.Equals(
                generation.OwnerExtensionId,
                GetServiceOwner(generation.Configuration.Id),
                StringComparison.Ordinal))
        {
            var removed = false;
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Active, generation))
                {
                    slot.Active = null;
                    generation.Ready = false;
                    removed = true;
                }
            }

            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            if (removed)
            {
                PublishRestartBailoutState(generation);
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
            }

            return;
        }

        if (!_runtimeState.NewServicesAllowed)
        {
            var removed = false;
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Active, generation))
                {
                    slot.Active = null;
                    generation.Ready = false;
                    removed = true;
                }
            }

            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            if (removed)
            {
                PublishRestartBailoutState(generation, ExtensionServiceFailureCode.RuntimeUnavailable);
                PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "unavailable");
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
            }

            return;
        }

        Task<HostServiceReadinessResult>? existingStartup = null;
        Task<SupervisorOperationResult>? startTask = null;
        var dependencyBindingsUnavailable = false;
        var configurationSuperseded = false;
        lock (_lifecycleGate)
        {
            if (IsStopping)
            {
                return;
            }

            var currentSnapshot = LatestSnapshot(snapshot);
            var currentService = currentSnapshot.Services.FirstOrDefault(value =>
                value.Id == generation.Configuration.Id);
            if (!IsCurrentCrashRestartConfiguration(currentSnapshot, currentService, generation))
            {
                snapshot = currentSnapshot;
                configuredService = currentService;
                configurationSuperseded = true;
            }
            else
            {
                snapshot = currentSnapshot;
                configuredService = currentService!;
                lock (slot.Gate)
                {
                    if (IsStopping || !ReferenceEquals(slot.Active, generation))
                    {
                        return;
                    }

                    if (slot.Startup is { } startup)
                    {
                        existingStartup = startup;
                    }
                    else if (!HasCurrentCrashRestartLaunchInputs(snapshot, configuredService, generation))
                    {
                        dependencyBindingsUnavailable = true;
                    }
                    else
                    {
                        generation.ProcessExitRecorded = false;
                        generation.ProcessExitCode = null;
                        PublishRuntimeSnapshot(generation, lifecycleState: ExtensionServiceLifecycleState.Starting);
                        startTask = StartSupervisorWithPendingLeaseCleanupAsync(
                            generation,
                            DateTimeOffset.UtcNow,
                            _shutdownCts.Token);
                    }
                }
            }
        }

        if (configurationSuperseded)
        {
            await RetireCrashRestartGenerationAsync(
                slot,
                generation,
                snapshot,
                configuredService,
                dependenciesUnavailable: false).ConfigureAwait(false);
            return;
        }

        if (dependencyBindingsUnavailable)
        {
            await RetireCrashRestartGenerationAsync(
                slot,
                generation,
                snapshot,
                configuredService,
                dependenciesUnavailable: true).ConfigureAwait(false);
            return;
        }

        if (existingStartup is not null)
        {
            try
            {
                await existingStartup.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(RestartAfterAsync));
            }

            if (IsStopping ||
                !IsCurrentGeneration(slot, generation) ||
                generation.Supervisor.ActiveProcessInstance is not null)
            {
                return;
            }

            lock (_lifecycleGate)
            {
                if (IsStopping)
                {
                    return;
                }

                var currentSnapshot = LatestSnapshot(snapshot);
                var currentService = currentSnapshot.Services.FirstOrDefault(value =>
                    value.Id == generation.Configuration.Id);
                if (!IsCurrentCrashRestartConfiguration(currentSnapshot, currentService, generation))
                {
                    snapshot = currentSnapshot;
                    configuredService = currentService;
                    configurationSuperseded = true;
                }
                else
                {
                    snapshot = currentSnapshot;
                    configuredService = currentService!;
                    lock (slot.Gate)
                    {
                        if (IsStopping ||
                            !ReferenceEquals(slot.Active, generation) ||
                            generation.Supervisor.ActiveProcessInstance is not null)
                        {
                            return;
                        }

                        if (!HasCurrentCrashRestartLaunchInputs(snapshot, configuredService, generation))
                        {
                            dependencyBindingsUnavailable = true;
                        }
                        else
                        {
                            generation.ProcessExitRecorded = false;
                            generation.ProcessExitCode = null;
                            PublishRuntimeSnapshot(generation, lifecycleState: ExtensionServiceLifecycleState.Starting);
                            startTask = StartSupervisorWithPendingLeaseCleanupAsync(
                                generation,
                                DateTimeOffset.UtcNow,
                                _shutdownCts.Token);
                        }
                    }
                }
            }

            if (configurationSuperseded)
            {
                await RetireCrashRestartGenerationAsync(
                    slot,
                    generation,
                    snapshot,
                    configuredService,
                    dependenciesUnavailable: false).ConfigureAwait(false);
                return;
            }

            if (dependencyBindingsUnavailable)
            {
                await RetireCrashRestartGenerationAsync(
                    slot,
                    generation,
                    snapshot,
                    configuredService,
                    dependenciesUnavailable: true).ConfigureAwait(false);
                return;
            }

        }

        if (startTask is null)
        {
            return;
        }

        SupervisorOperationResult started;
        try
        {
            started = await startTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(RestartAfterAsync));
            var failedState = generation.Supervisor.Snapshot;
            PublishRuntimeSnapshot(
                generation,
                failedState,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                ExtensionServiceFailureCode.Unknown);
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        slot.ObserveStartAttemptNumber(generation.Supervisor.StartAttemptNumber);


        if (started.Snapshot.ObservedLifecycle == ServiceLifecycleState.Waiting)
        {
            var failureCode = MapFailureCode(started.Reason);
            PublishRuntimeSnapshot(
                generation,
                started.Snapshot,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceFailureStage.Spawn,
                failureCode == ExtensionServiceFailureCode.None
                    ? ExtensionServiceFailureCode.Unknown
                    : failureCode,
                retryAt: started.Snapshot.Deadline?.At,
                preserveServiceVersion: true);
            return;
        }

        if (started.Status != SupervisorOperationStatus.Applied || generation.Supervisor.Lease is null)
        {
            var failureCode = MapFailureCode(started.Reason);
            PublishRuntimeSnapshot(
                generation,
                started.Snapshot,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                failureCode == ExtensionServiceFailureCode.None
                    ? ExtensionServiceFailureCode.StartRejected
                    : failureCode);
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (!_serviceLogBufferRegistry.HasOutputTap &&
            generation.Supervisor.ActiveProcessInstance is { } processInstanceId)
        {
            _serviceLogBufferRegistry.OnGenerationStarted(
                generation.Configuration.Id,
                processInstanceId,
                generation.Supervisor.StartAttemptNumber,
                started.Snapshot.ChangedAt);
        }

        generation.ProcessExitRecorded = false;
        generation.ProcessExitCode = null;
        PublishRuntimeSnapshot(generation, started.Snapshot, ExtensionServiceLifecycleState.Starting);

        if (IsStopping)
        {
            return;
        }

        var healthy = await WaitForHealthyAsync(slot, snapshot, generation, cancellationToken).ConfigureAwait(false);
        if (healthy is not { } ready)
        {
            if (IsStopping || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var failedState = generation.Supervisor.Snapshot;
            bool processExited;
            int? processExitCode;
            lock (slot.Gate)
            {
                processExited = generation.ProcessExitRecorded;
                processExitCode = generation.ProcessExitCode;
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
            PublishRuntimeSnapshot(
                generation,
                failedState,
                ExtensionServiceLifecycleState.Failed,
                failureStage,
                failureCode,
                processExitCode: processExitCode,
                preserveServiceVersion: true);
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            PublishRuntimeSnapshot(
                generation,
                generation.Supervisor.Snapshot,
                ExtensionServiceLifecycleState.Failed,
                failureStage,
                failureCode,
                processExitCode: processExitCode,
                preserveServiceVersion: true);
            var removed = false;
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Active, generation))
                {
                    slot.Active = null;
                    removed = true;
                }
            }

            if (removed)
            {
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "stopped");
            }

            return;
        }

        lock (slot.Gate)
        {
            if (IsStopping || !ReferenceEquals(slot.Active, generation))
            {
                return;
            }

            generation.Lease = ready.Lease;
            generation.HealthRetryState = ready.Retry;
            generation.Ready = true;
        }

        PublishRuntimeSnapshot(generation, lifecycleState: ExtensionServiceLifecycleState.Running);
        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        await RefreshChangedActiveGraphAsync(snapshot, CancellationToken.None, forceRetry: true).ConfigureAwait(false);
        HostLogMessages.ServiceReady(_logger, generation.Configuration.Id, generation.SnapshotVersion);
        PublishServiceState(
            generation.Configuration.Id,
            generation.SnapshotVersion,
            "ready");
    }


    private bool IsCurrentCrashRestartConfiguration(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration? service,
        ServiceGeneration generation) =>
        service is { Enabled: true } &&
        service.Version == generation.Configuration.Version &&
        IsServiceEnabledForSnapshot(snapshot, service.Id) &&
        string.Equals(
            generation.OwnerExtensionId,
            GetServiceOwner(service.Id),
            StringComparison.Ordinal);

    private bool HasCurrentCrashRestartLaunchInputs(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ServiceGeneration generation)
    {
        var dependencyIds = ServiceLaunchTemplate.ExtractDependencies(
            service.ArgumentList.Cast<string?>().Concat(service.Environment.Values));
        if (dependencyIds.Contains(service.Id) ||
            !dependencyIds.SetEquals(generation.DependencyBindings.Keys))
        {
            return false;
        }

        var active = ImmutableDictionary.CreateBuilder<Guid, ServiceGeneration>();
        foreach (var dependencyId in dependencyIds)
        {
            if (!_slots.TryGetValue(dependencyId, out var dependencySlot))
            {
                return false;
            }

            lock (dependencySlot.Gate)
            {
                if (dependencySlot.Active is not { Ready: true } dependencyGeneration)
                {
                    return false;
                }

                active[dependencyId] = dependencyGeneration;
            }
        }

        var activeGenerations = active.ToImmutable();
        foreach (var dependencyId in dependencyIds)
        {
            var configuredDependency = snapshot.Services.FirstOrDefault(value => value.Id == dependencyId);
            if (configuredDependency is not { Enabled: true } ||
                !IsServiceEnabledForSnapshot(snapshot, dependencyId) ||
                !activeGenerations.TryGetValue(dependencyId, out var dependencyGeneration) ||
                dependencyGeneration.Configuration.Version != configuredDependency.Version ||
                !string.Equals(
                    dependencyGeneration.OwnerExtensionId,
                    GetServiceOwner(dependencyId),
                    StringComparison.Ordinal) ||
                dependencyGeneration.Supervisor.Snapshot.ObservedLifecycle != ServiceLifecycleState.Running ||
                dependencyGeneration.Supervisor.ActiveProcessInstance is null)
            {
                return false;
            }
        }

        return TryBuildActiveDependencyBindings(
                snapshot,
                dependencyIds,
                activeGenerations,
                out var dependencyBindings) &&
            generation.Lease is { } lease &&
            TryResolveLaunchInputs(service, lease.Port, dependencyBindings, out var launchInputs) &&
            LaunchInputsEqual(generation, launchInputs);
    }

    private async Task RetireCrashRestartGenerationAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration? service,
        bool dependenciesUnavailable)
    {
        var removed = false;
        lock (slot.Gate)
        {
            if (ReferenceEquals(slot.Active, generation))
            {
                slot.Active = null;
                generation.Ready = false;
                removed = true;
            }
        }

        if (!removed)
        {
            return;
        }

        if (dependenciesUnavailable)
        {
            PublishRuntimeFailure(
                slot,
                snapshot,
                service ?? generation.Configuration,
                generation,
                generation.Supervisor.Snapshot,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceFailureStage.Spawn,
                ExtensionServiceFailureCode.DependencyUnavailable);
        }

        await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
        if (dependenciesUnavailable)
        {
            PublishServiceState(generation.Configuration.Id, snapshot.Version, "unavailable");
        }
        else
        {
            PublishRestartBailoutState(generation);
        }

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        if (dependenciesUnavailable)
        {
            await EnsureReadyAsync(
                snapshot,
                generation.Configuration.Id,
                CancellationToken.None).ConfigureAwait(false);
        }
    }
    private async Task RestartTerminalAfterAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            await RestartTerminalAfterCoreAsync(slot, generation, notBefore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoveRetiringGeneration(generation);
            HostLogMessages.LifecycleBackgroundCancelled(
                _logger,
                nameof(RestartTerminalAfterAsync),
                generation.Configuration.Id);
        }
        catch (Exception exception)
        {
            HostLogMessages.LifecycleBackgroundFailed(
                _logger,
                exception,
                nameof(RestartTerminalAfterAsync),
                generation.Configuration.Id);
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
        }
    }

    private async Task RestartTerminalAfterCoreAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        if (IsStopping)
        {
            return;
        }

        var delay = notBefore - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (IsStopping)
        {
            RemoveRetiringGeneration(generation);
            return;
        }

        var snapshot = _snapshotHolder.Current;
        var configuredService = snapshot?.Services.FirstOrDefault(value =>
            value.Id == generation.Configuration.Id);
        if (snapshot is null ||
            configuredService is not { Enabled: true } ||
            configuredService.Version != generation.Configuration.Version ||
            !IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id))
        {
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
            PublishRestartBailoutState(generation);
            return;
        }

        if (!_runtimeState.NewServicesAllowed)
        {
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
            PublishRestartBailoutState(generation, ExtensionServiceFailureCode.RuntimeUnavailable);
            PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "unavailable");
            return;
        }

        // A request-triggered startup may already own slot.Startup. Share it rather than starting a second candidate.
        // If that startup fails without changing slot.Active, make one backoff-owned attempt below.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Task<HostServiceReadinessResult> startup;
            var ownsStartup = false;
            lock (_lifecycleGate)
            {
                if (IsStopping)
                {
                    RemoveRetiringGeneration(generation);
                    return;
                }

                lock (slot.Gate)
                {
                    if (IsStopping || !ReferenceEquals(slot.Active, generation))
                    {
                        RemoveRetiringGeneration(generation);
                        return;
                    }

                    if (slot.Startup is { } existingStartup)
                    {
                        startup = existingStartup;
                    }
                    else
                    {
                        ownsStartup = true;
                        startup = StartOrSwitchAsync(
                            slot,
                            snapshot,
                            generation.Configuration,
                            ImmutableHashSet<Guid>.Empty,
                            stopReplacedGeneration: false);
                    }
                }
            }

            try
            {
                await startup.ConfigureAwait(false);
            }
            catch
            {
                await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
                return;
            }

            if (IsStopping)
            {
                RemoveRetiringGeneration(generation);
                return;
            }

            ServiceGeneration? active;
            lock (slot.Gate)
            {
                active = slot.Active;
            }

            if (ReferenceEquals(active, generation))
            {
                if (ownsStartup || attempt == 1)
                {
                    await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            // A shared request startup already performed the replacement and its normal path owns old-generation cleanup.
            if (!ownsStartup || active is null)
            {
                RemoveRetiringGeneration(generation);
                return;
            }

            await RefreshChangedActiveGraphAsync(snapshot, CancellationToken.None, forceRetry: true).ConfigureAwait(false);
            await StopRetiringGenerationAsync(slot, generation).ConfigureAwait(false);
            return;
        }
    }

    private void PublishRestartBailoutState(
        ServiceGeneration generation,
        ExtensionServiceFailureCode? failureCode = null)
    {
        var snapshot = _snapshotHolder.Current;
        if (snapshot is null)
        {
            PublishRuntimeSnapshot(
                generation,
                generation.Supervisor.Snapshot,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                failureCode ?? ExtensionServiceFailureCode.RuntimeUnavailable,
                preserveServiceVersion: true);
            SynchronizePublishedRuntimeConfiguration();
            return;
        }

        var service = snapshot.Services.FirstOrDefault(value => value.Id == generation.Configuration.Id);
        if (service is null)
        {
            SynchronizePublishedRuntimeConfiguration();
            return;
        }

        var enabled = service.Enabled && IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id);
        if (failureCode is { } failure && enabled && service.Version == generation.Configuration.Version)
        {
            PublishRuntimeSnapshot(
                generation,
                generation.Supervisor.Snapshot,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                failure,
                configurationVersion: snapshot.Version,
                preserveServiceVersion: true);
        }
        else
        {
            PublishConfiguredRuntimeState(
                snapshot,
                service,
                enabled ? ExtensionServiceLifecycleState.Stopped : ExtensionServiceLifecycleState.Disabled);
        }

        SynchronizePublishedRuntimeConfiguration();
    }

    private async Task StopRetiringGenerationAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        try
        {
            await StopRetiringGenerationCoreAsync(slot, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RemoveRetiringGeneration(generation);
            HostLogMessages.LifecycleBackgroundCancelled(
                _logger,
                nameof(StopRetiringGenerationAsync),
                generation.Configuration.Id);
        }
        catch (Exception exception)
        {
            RemoveRetiringGeneration(generation);
            HostLogMessages.LifecycleBackgroundFailed(
                _logger,
                exception,
                nameof(StopRetiringGenerationAsync),
                generation.Configuration.Id);
        }
    }

    private async Task StopRetiringGenerationCoreAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        try
        {
            await DrainAndStopGenerationAsync(slot, generation).ConfigureAwait(false);
        }
        finally
        {
            RemoveRetiringGeneration(generation);
        }

        if (IsStopping)
        {
            return;
        }

        var removed = false;
        lock (slot.Gate)
        {
            if (ReferenceEquals(slot.Active, generation))
            {
                slot.Active = null;
                removed = true;
            }
        }

        if (removed)
        {
            await PublishReadyEndpointsAsync().ConfigureAwait(false);
        }

        PublishServiceState(
            generation.Configuration.Id,
            generation.SnapshotVersion,
            "stopped");
    }

    private async Task<SupervisorOperationResult> StopOrReleaseGenerationAfterExitAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        CancellationToken cancellationToken)
    {
        if (generation.ProcessExitRecorded && generation.Supervisor.ActiveProcessInstance is null)
        {
            var acknowledged = await AcknowledgeProcessExitAndRetainLeaseAsync(generation).ConfigureAwait(false);

            generation.Lease = null;
            return acknowledged;
        }

        return await StopGenerationAsync(slot, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAndStopGenerationAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        if (_retiringGenerations.TryGetValue(generation, out var retiring) &&
            !TryClaimRetiringStop(retiring))
        {
            if (Volatile.Read(ref retiring.Outcome) == RetiringGenerationState.ProcessExited)
            {
                await retiring.ProcessExitAcknowledged.Task.ConfigureAwait(false);
            }

            return;
        }

        if (generation.ProcessExitRecorded && generation.Supervisor.ActiveProcessInstance is null)
        {
            var acknowledged = await AcknowledgeProcessExitAndRetainLeaseAsync(generation).ConfigureAwait(false);

            generation.Lease = null;
            return;
        }

        if (generation.Lease is { } lease)
        {
            await _drainTracker.WaitDrainedAsync(
                generation.Configuration.Id,
                lease.Port,
                StopGracePeriod,
                CancellationToken.None).ConfigureAwait(false);
        }

        await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
    }


    private void RemoveRetiringGeneration(ServiceGeneration generation) =>
        _retiringGenerations.TryRemove(generation, out _);

    private static bool TryClaimRetiringProcessExit(RetiringGenerationState state) =>
        Interlocked.CompareExchange(
            ref state.Outcome,
            RetiringGenerationState.ProcessExited,
            RetiringGenerationState.Pending) == RetiringGenerationState.Pending;

    private static bool TryClaimRetiringStop(RetiringGenerationState state) =>
        Interlocked.CompareExchange(
            ref state.Outcome,
            RetiringGenerationState.StopClaimed,
            RetiringGenerationState.Pending) == RetiringGenerationState.Pending;

    private sealed class RetiringGenerationState
    {
        internal const int Pending = 0;
        internal const int ProcessExited = 1;
        internal const int StopClaimed = 2;

        internal int Outcome;
        internal TaskCompletionSource<bool> ProcessExitAcknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
