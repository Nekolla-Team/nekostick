using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

/// <summary>Exposes only immutable runtime telemetry from active Host service generations.</summary>
public interface IHostServiceRuntimeSnapshotAccessor
{
    /// <summary>Reads a consistent immutable snapshot of active service generations.</summary>
    ImmutableArray<HostServiceRuntimeSnapshot> ReadCurrent();

    /// <summary>Reads one active generation without exposing supervision handles.</summary>
    bool TryGet(Guid serviceId, out HostServiceRuntimeSnapshot snapshot);
}

/// <summary>Host-owned internal runtime representation used to compose extension DTOs.</summary>
public sealed record HostServiceRuntimeSnapshot
{
    internal HostServiceRuntimeSnapshot(
        Guid serviceId,
        long configurationVersion,
        int? processId,
        ProcessInstanceId? processInstanceId,
        DateTimeOffset? startedAt,
        DateTimeOffset? lastUpdatedAt,
        DateTimeOffset? lastHealthAt,
        ExtensionServiceLifecycleState lifecycleState,
        ExtensionServiceHealthState healthState,
        string? ownerExtensionId = null,
        ExtensionServiceFailureStage failureStage = ExtensionServiceFailureStage.None,
        ExtensionServiceFailureCode failureCode = ExtensionServiceFailureCode.None,
        string? failureReason = null,
        ExtensionServiceProbeSnapshot? lastProbe = null,
        int? processExitCode = null,
        int restartCount = 0,
        DateTimeOffset? stateEnteredAt = null,
        DateTimeOffset? retryAt = null,
        Guid? generationId = null)
    {
        ServiceId = serviceId;
        ConfigurationVersion = configurationVersion;
        ProcessId = processId;
        ProcessInstanceId = processInstanceId;
        StartedAt = startedAt;
        LastUpdatedAt = lastUpdatedAt;
        LastHealthAt = lastHealthAt;
        LifecycleState = lifecycleState;
        Health = healthState;
        OwnerExtensionId = ownerExtensionId;
        FailureStage = failureStage;
        FailureCode = failureCode;
        FailureReason = failureReason;
        LastProbe = lastProbe;
        ProcessExitCode = processExitCode;
        RestartCount = restartCount;
        StateEnteredAt = stateEnteredAt ?? lastUpdatedAt;
        RetryAt = retryAt;
        GenerationId = generationId;
    }

    /// <summary>Gets the identifier of the service represented by this snapshot.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the immutable Host configuration generation paired with this snapshot.</summary>
    public long ConfigurationVersion { get; }

    /// <summary>Gets the identifier of the active service process, if available.</summary>
    public int? ProcessId { get; }
    /// <summary>Gets the time at which the active service process started, if available.</summary>
    public DateTimeOffset? StartedAt { get; }
    /// <summary>Gets the opaque identity of the active process generation, if available.</summary>
    public ProcessInstanceId? ProcessInstanceId { get; }

    /// <summary>Gets the time of the most recent lifecycle or health state update represented by this snapshot.</summary>
    public DateTimeOffset? LastUpdatedAt { get; }

    /// <summary>Gets the time of the most recent health observation, if available.</summary>
    public DateTimeOffset? LastHealthAt { get; }

    /// <summary>Gets the current lifecycle state of the service.</summary>
    public ExtensionServiceLifecycleState LifecycleState { get; }

    /// <summary>Gets the current health state of the service.</summary>
    public ExtensionServiceHealthState Health { get; }

    /// <summary>Gets the owning extension identifier when this service is extension-owned.</summary>
    public string? OwnerExtensionId { get; }
    /// <summary>Gets the lifecycle stage where the current failure occurred.</summary>
    public ExtensionServiceFailureStage FailureStage { get; }

    /// <summary>Gets the safe machine-readable failure reason.</summary>
    public ExtensionServiceFailureCode FailureCode { get; }

    /// <summary>Gets the bounded human-readable failure explanation.</summary>
    public string? FailureReason { get; }

    /// <summary>Gets the latest health probe result and safe details.</summary>
    public ExtensionServiceProbeSnapshot? LastProbe { get; }

    /// <summary>Gets the process exit code, when an exit was observed.</summary>
    public int? ProcessExitCode { get; }

    /// <summary>Gets the number of restart attempts recorded for the service.</summary>
    public int RestartCount { get; }

    /// <summary>Gets the UTC time at which the current lifecycle state began.</summary>
    public DateTimeOffset? StateEnteredAt { get; }

    /// <summary>Gets the UTC time of the next scheduled attempt.</summary>
    public DateTimeOffset? RetryAt { get; }
    internal Guid? GenerationId { get; }

    internal HostServiceRuntimeSnapshot WithStateEnteredAt(DateTimeOffset? stateEnteredAt) => new(
        ServiceId,
        ConfigurationVersion,
        ProcessId,
        ProcessInstanceId,
        StartedAt,
        LastUpdatedAt,
        LastHealthAt,
        LifecycleState,
        Health,
        OwnerExtensionId,
        FailureStage,
        FailureCode,
        FailureReason,
        LastProbe,
        ProcessExitCode,
        RestartCount,
        stateEnteredAt,
        RetryAt,
        GenerationId);

    internal HostServiceRuntimeSnapshot WithOwnerExtensionId(string? ownerExtensionId, DateTimeOffset lastUpdatedAt) => new(
        ServiceId,
        ConfigurationVersion,
        ProcessId,
        ProcessInstanceId,
        StartedAt,
        lastUpdatedAt,
        LastHealthAt,
        LifecycleState,
        Health,
        ownerExtensionId,
        FailureStage,
        FailureCode,
        FailureReason,
        LastProbe,
        ProcessExitCode,
        RestartCount,
        StateEnteredAt,
        RetryAt,
        GenerationId);
    internal HostServiceRuntimeSnapshot WithConfigurationVersion(
        long configurationVersion,
        string? ownerExtensionId,
        DateTimeOffset lastUpdatedAt) => new(
            ServiceId,
            configurationVersion,
            ProcessId,
            ProcessInstanceId,
            StartedAt,
            lastUpdatedAt,
            LastHealthAt,
            LifecycleState,
            Health,
            ownerExtensionId,
            FailureStage,
            FailureCode,
            FailureReason,
            LastProbe,
            ProcessExitCode,
            RestartCount,
            StateEnteredAt,
        RetryAt,
        GenerationId);
    internal HostServiceRuntimeSnapshot WithRetryAt(DateTimeOffset? retryAt) => new(
        ServiceId,
        ConfigurationVersion,
        ProcessId,
        ProcessInstanceId,
        StartedAt,
        LastUpdatedAt,
        LastHealthAt,
        LifecycleState,
        Health,
        OwnerExtensionId,
        FailureStage,
        FailureCode,
        FailureReason,
        LastProbe,
        ProcessExitCode,
        RestartCount,
        StateEnteredAt,
        retryAt,
        GenerationId);

    internal HostServiceRuntimeSnapshot WithRestartCount(int restartCount) => new(
        ServiceId,
        ConfigurationVersion,
        ProcessId,
        ProcessInstanceId,
        StartedAt,
        LastUpdatedAt,
        LastHealthAt,
        LifecycleState,
        Health,
        OwnerExtensionId,
        FailureStage,
        FailureCode,
        FailureReason,
        LastProbe,
        ProcessExitCode,
        restartCount,
        StateEnteredAt,
        RetryAt,
        GenerationId);



    /// <summary>Gets the non-negative elapsed time since the active service process started, or <see langword="null"/> if unavailable.</summary>
    public TimeSpan? Uptime
    {
        get
        {
            if (StartedAt is not { } started)
            {
                return null;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            return elapsed >= TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }
    }
}


/// <summary>Publishes node-local runtime state through the narrow telemetry accessor.</summary>
public sealed partial class HostServiceLifecycleManager : IHostServiceRuntimeSnapshotAccessor
{
    /// <inheritdoc />
    public ImmutableArray<HostServiceRuntimeSnapshot> ReadCurrent()
    {
        SynchronizePublishedRuntimeConfiguration();
        return _endpointPublisher.CommittedView.RuntimeSnapshots.Values
            .OrderBy(static snapshot => snapshot.ServiceId)
            .ToImmutableArray();
    }

    /// <inheritdoc />
    public bool TryGet(Guid serviceId, out HostServiceRuntimeSnapshot snapshot)
    {
        SynchronizePublishedRuntimeConfiguration();
        return _endpointPublisher.CommittedView.RuntimeSnapshots.TryGetValue(serviceId, out snapshot!);
    }

    private void PublishRuntimeSnapshot(
        ServiceGeneration generation,
        ServiceRuntimeSnapshot? state = null,
        ExtensionServiceLifecycleState? lifecycleState = null,
        ExtensionServiceFailureStage failureStage = ExtensionServiceFailureStage.None,
        ExtensionServiceFailureCode failureCode = ExtensionServiceFailureCode.None,
        string? failureReason = null,
        int? processExitCode = null,
        DateTimeOffset? retryAt = null,
        DateTimeOffset? updatedAt = null,
        bool preserveFailure = false,
        long? configurationVersion = null,
        long? serviceVersion = null,
        ServiceHealthState? healthOverride = null,
        HealthObservationResult? lastProbeOverride = null,
        ServiceStateReasonCode? probeFailureReasonOverride = null,
        bool preserveServiceVersion = false,
        int? restartCountIncrementOverride = null,
        bool preserveLastProbe = false)
    {
        if (generation.GraphPreparation)
        {
            return;
        }
        var supervisor = generation.Supervisor;
        var current = state ?? supervisor.Snapshot;
        var lifecycle = lifecycleState ?? MapLifecycle(current.ObservedLifecycle);
        if (lifecycleState is null && current.ObservedLifecycle == ServiceLifecycleState.Starting &&
            current.Deadline is { Kind: ServiceDeadlineKind.RestartBackoff or ServiceDeadlineKind.WaitingBackoff })
        {
            lifecycle = ExtensionServiceLifecycleState.Waiting;
        }

        var hasPrevious = _runtimeRegistry.TryGet(current.ServiceId, out var previous);
        if (preserveFailure && hasPrevious && previous.LifecycleState == lifecycle &&
            (previous.FailureStage != ExtensionServiceFailureStage.None ||
             previous.FailureCode != ExtensionServiceFailureCode.None))
        {
            failureStage = previous.FailureStage;
            failureCode = previous.FailureCode;
            failureReason = previous.FailureReason;
            processExitCode ??= previous.ProcessExitCode;
            retryAt ??= previous.RetryAt;
        }

        var health = MapHealth(healthOverride ?? current.Health);
        var hasProcess = supervisor.TryGetActiveProcessTelemetry(
            out var processInstanceId,
            out var processId,
            out var startedAt);
        var observation = lastProbeOverride ?? current.LastHealthObservation;
        var lastHealthAt = observation?.ObservedAt;
        var lastProbe = observation is null
            ? hasPrevious ? previous.LastProbe : null
            : new ExtensionServiceProbeSnapshot(
                observation.ObservedAt,
                MapProbeResult(observation.Status),
                observation.Target,
                MapProbeFailure(observation.Status, probeFailureReasonOverride ?? generation.LastHealthProbeReason),
                observation.ErrorMessage is { Length: > 512 } probeErrorMessage
                    ? probeErrorMessage[..512]
                    : observation.ErrorMessage);
        if (preserveFailure && hasPrevious && previous.LifecycleState == lifecycle &&
            (previous.FailureStage != ExtensionServiceFailureStage.None ||
             previous.FailureCode != ExtensionServiceFailureCode.None))
        {
            lastProbe = previous.LastProbe;
            lastHealthAt = previous.LastHealthAt;
        }
        if (preserveLastProbe && hasPrevious)
        {
            lastProbe = previous.LastProbe;
            lastHealthAt = previous.LastHealthAt;
        }
        if (lastHealthAt is null && hasPrevious)
        {
            lastHealthAt = previous.LastHealthAt;
        }

        var lastUpdatedAt = current.ChangedAt;
        if (updatedAt is { } updated && updated > lastUpdatedAt)
        {
            lastUpdatedAt = updated;
        }

        if (startedAt is { } started && started > lastUpdatedAt)
        {
            lastUpdatedAt = started;
        }

        if (lastHealthAt is { } healthAt && healthAt > lastUpdatedAt)
        {
            lastUpdatedAt = healthAt;
        }

        var restartCountIncrement = restartCountIncrementOverride ??
            generation.RecordRestartAttemptDelta(current.RestartAttempts);

        var resolvedRetryAt = retryAt ?? (lifecycle == ExtensionServiceLifecycleState.Waiting
            ? current.Deadline?.At
            : null);
        _runtimeRegistry.Publish(
            new HostServiceRuntimeSnapshot(
                current.ServiceId,
                configurationVersion ?? generation.SnapshotVersion,
                hasProcess ? processId : null,
                hasProcess ? processInstanceId : null,
                hasProcess ? startedAt : null,
                lastUpdatedAt,
                lastHealthAt,
                lifecycle,
                health,
                generation.OwnerExtensionId,
                failureStage,
                failureCode,
                failureReason ?? DescribeFailure(failureCode),
                lastProbe,
                processExitCode,
                0,
                retryAt: resolvedRetryAt,
                generationId: generation.GenerationId),
            serviceVersion ?? generation.Configuration.Version,
            enabled: true,
            preserveServiceVersion: preserveServiceVersion,
            restartCountIncrement: restartCountIncrement);
    }

    private void PublishConfiguredRuntimeState(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ExtensionServiceLifecycleState lifecycleState,
        ExtensionServiceFailureStage failureStage = ExtensionServiceFailureStage.None,
        ExtensionServiceFailureCode failureCode = ExtensionServiceFailureCode.None,
        string? failureReason = null,
        DateTimeOffset? retryAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var owner = _snapshotHolder.RoutingSnapshot?.ServiceOwners.TryGetValue(service.Id, out var serviceOwner) == true
            ? serviceOwner
            : null;
        _runtimeRegistry.TryGet(service.Id, out var previous);
        _runtimeRegistry.Publish(
            new HostServiceRuntimeSnapshot(
                service.Id,
                snapshot.Version,
                null,
                null,
                null,
                now,
                previous?.LastHealthAt,
                lifecycleState,
                ExtensionServiceHealthState.Unknown,
                owner,
                failureStage,
                failureCode,
                failureReason ?? DescribeFailure(failureCode),
                lifecycleState == ExtensionServiceLifecycleState.Disabled ? null : previous?.LastProbe,
                processExitCode: null,
                restartCount: previous?.RestartCount ?? 0,
                retryAt: retryAt),
            service.Version,
            enabled: lifecycleState != ExtensionServiceLifecycleState.Disabled);
    }

    private void PublishConfiguredRuntimeTransition(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ExtensionServiceLifecycleState lifecycleState)
    {
        ServiceGeneration? active;
        lock (slot.Gate)
        {
            if (slot.GraphPreparation)
            {
                return;
            }

            active = slot.Active;
        }

        if (active is { Ready: true } &&
            active.Supervisor.Snapshot.ObservedLifecycle == ServiceLifecycleState.Running)
        {
            var liveState = active.Supervisor.Snapshot;
            PublishRuntimeSnapshot(
                active,
                liveState,
                ExtensionServiceLifecycleState.Running,
                configurationVersion: snapshot.Version,
                serviceVersion: service.Version,
                healthOverride: liveState.Health);
            return;
        }

        PublishConfiguredRuntimeState(snapshot, service, lifecycleState);
    }

    private void PublishCandidateRuntimeObservation(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceGeneration attempt,
        ServiceRuntimeSnapshot state,
        ExtensionServiceLifecycleState lifecycleState,
        ServiceStateReasonCode? probeFailureReason = null)
    {
        ServiceGeneration? active;
        lock (slot.Gate)
        {
            if (slot.GraphPreparation || attempt.GraphPreparation)
            {
                return;
            }

            active = slot.Active;
        }

        if (active is { Ready: true } && !ReferenceEquals(active, attempt) &&
            active.Supervisor.Snapshot.ObservedLifecycle == ServiceLifecycleState.Running)
        {
            var liveState = active.Supervisor.Snapshot;
            PublishRuntimeSnapshot(
                active,
                liveState,
                ExtensionServiceLifecycleState.Running,
                updatedAt: state.ChangedAt,
                configurationVersion: snapshot.Version,
                serviceVersion: attempt.Configuration.Version,
                healthOverride: liveState.Health,
                lastProbeOverride: state.LastHealthObservation,
                probeFailureReasonOverride: probeFailureReason ?? attempt.LastHealthProbeReason);
            return;
        }

        PublishRuntimeSnapshot(
            attempt,
            state,
            lifecycleState,
            probeFailureReasonOverride: probeFailureReason ?? attempt.LastHealthProbeReason);
    }

    private void PublishActiveRuntimeState(ServiceSlot slot, HostConfigurationSnapshot snapshot)
    {
        ServiceGeneration? active;
        lock (slot.Gate)
        {
            active = slot.Active;
        }

        if (active is null)
        {
            return;
        }

        PublishRuntimeSnapshot(
            active,
            updatedAt: DateTimeOffset.UtcNow,
            preserveFailure: true,
            configurationVersion: snapshot.Version,
            serviceVersion: active.Configuration.Version,
            preserveServiceVersion: true);
    }

    private void PublishRuntimeFailure(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        ServiceGeneration? candidate,
        ServiceRuntimeSnapshot? failureState,
        ExtensionServiceLifecycleState lifecycleState,
        ExtensionServiceFailureStage failureStage,
        ExtensionServiceFailureCode failureCode,
        int? processExitCode = null,
        DateTimeOffset? retryAt = null,
        DateTimeOffset? updatedAt = null,
        int? attemptNumber = null)
    {
        ServiceGeneration? active;
        var attemptedState = failureState ?? candidate?.Supervisor.Snapshot;
        lock (slot.Gate)
        {
            if (candidate?.GraphPreparation == true || (candidate is null && slot.GraphPreparation))
            {
                return;
            }

            active = slot.Active;
            attemptNumber ??= slot.StartAttemptNumber;
        }
        var restartCountIncrement = candidate is not null && attemptedState is not null
            ? candidate.RecordRestartAttemptDelta(attemptedState.RestartAttempts)
            : (int?)null;

        if (attemptNumber is > 0 &&
            failureStage != ExtensionServiceFailureStage.None &&
            failureCode != ExtensionServiceFailureCode.None)
        {
            _serviceLogBufferRegistry.RecordStartupFailure(
                service.Id,
                attemptNumber.Value,
                candidate?.Supervisor.ActiveProcessInstance,
                failureStage,
                failureCode,
                DescribeFailure(failureCode),
                updatedAt ?? attemptedState?.ChangedAt ?? DateTimeOffset.UtcNow);
        }

        if (active is { Ready: true } && !ReferenceEquals(active, candidate) &&
            active.Supervisor.Snapshot.ObservedLifecycle == ServiceLifecycleState.Running)
        {
            var liveState = active.Supervisor.Snapshot;
            PublishRuntimeSnapshot(
                active,
                liveState,
                ExtensionServiceLifecycleState.Running,
                failureStage,
                failureCode,
                processExitCode: processExitCode,
                retryAt: retryAt,
                updatedAt: updatedAt ?? attemptedState?.ChangedAt ?? DateTimeOffset.UtcNow,
                configurationVersion: snapshot.Version,
                serviceVersion: service.Version,
                healthOverride: liveState.Health,
                lastProbeOverride: attemptedState?.LastHealthObservation,
                probeFailureReasonOverride: attemptedState?.Reason,
                restartCountIncrementOverride: restartCountIncrement);
            return;
        }

        if (candidate is not null)
        {
            PublishRuntimeSnapshot(
                candidate,
                failureState,
                lifecycleState,
                failureStage,
                failureCode,
                processExitCode: processExitCode,
                retryAt: retryAt,
                updatedAt: updatedAt,
                probeFailureReasonOverride: attemptedState?.Reason,
                restartCountIncrementOverride: restartCountIncrement);
            return;
        }

        PublishConfiguredRuntimeState(
            snapshot,
            service,
            lifecycleState,
            failureStage,
            failureCode,
            retryAt: retryAt);
    }

    private static ExtensionServiceLifecycleState MapLifecycle(ServiceLifecycleState lifecycle) => lifecycle switch
    {
        ServiceLifecycleState.Disabled => ExtensionServiceLifecycleState.Disabled,
        ServiceLifecycleState.Starting => ExtensionServiceLifecycleState.Starting,
        ServiceLifecycleState.Running => ExtensionServiceLifecycleState.Running,
        ServiceLifecycleState.Stopping => ExtensionServiceLifecycleState.Stopping,
        ServiceLifecycleState.Failed => ExtensionServiceLifecycleState.Failed,
        ServiceLifecycleState.Waiting => ExtensionServiceLifecycleState.Waiting,
        _ => ExtensionServiceLifecycleState.Unknown
    };

    private static ExtensionServiceHealthState MapHealth(ServiceHealthState health) => health switch
    {
        ServiceHealthState.Healthy => ExtensionServiceHealthState.Healthy,
        ServiceHealthState.Unhealthy => ExtensionServiceHealthState.Unhealthy,
        _ => ExtensionServiceHealthState.Unknown
    };

    private static ExtensionServiceProbeResult MapProbeResult(HealthObservationStatus status) => status switch
    {
        HealthObservationStatus.Healthy => ExtensionServiceProbeResult.Healthy,
        HealthObservationStatus.Unhealthy => ExtensionServiceProbeResult.Unhealthy,
        HealthObservationStatus.TimedOut => ExtensionServiceProbeResult.TimedOut,
        HealthObservationStatus.Cancelled => ExtensionServiceProbeResult.Cancelled,
        HealthObservationStatus.Unavailable => ExtensionServiceProbeResult.Unavailable,
        _ => ExtensionServiceProbeResult.Unknown
    };

    private static ExtensionServiceFailureCode MapProbeFailure(
        HealthObservationStatus status,
        ServiceStateReasonCode reason = ServiceStateReasonCode.None) =>
        (status, reason) switch
        {
            (HealthObservationStatus.Unavailable, ServiceStateReasonCode.PortLeaseUnavailable) =>
                ExtensionServiceFailureCode.PortLeaseUnavailable,
            (HealthObservationStatus.Unhealthy, _) => ExtensionServiceFailureCode.HealthCheckFailed,
            (HealthObservationStatus.TimedOut, _) => ExtensionServiceFailureCode.HealthTimeout,
            (HealthObservationStatus.Unavailable, _) => ExtensionServiceFailureCode.RuntimeUnavailable,
            (HealthObservationStatus.Cancelled, _) => ExtensionServiceFailureCode.Cancelled,
            _ => ExtensionServiceFailureCode.None
        };

    private static ExtensionServiceFailureCode MapFailureCode(ServiceStateReasonCode reason) => reason switch
    {
        ServiceStateReasonCode.StartRejected => ExtensionServiceFailureCode.StartRejected,
        ServiceStateReasonCode.InvalidLaunchSpecification => ExtensionServiceFailureCode.InvalidLaunchSpecification,
        ServiceStateReasonCode.MissingHostEnvironment => ExtensionServiceFailureCode.MissingHostEnvironment,
        ServiceStateReasonCode.ExecutableMissing => ExtensionServiceFailureCode.ExecutableMissing,
        ServiceStateReasonCode.PortLeaseUnavailable or
            ServiceStateReasonCode.PortLeaseConflict or
            ServiceStateReasonCode.PortLeaseExpired => ExtensionServiceFailureCode.PortLeaseUnavailable,
        ServiceStateReasonCode.DatabaseUnavailable => ExtensionServiceFailureCode.RuntimeUnavailable,
        ServiceStateReasonCode.HealthCheckFailed or
            ServiceStateReasonCode.HealthFailureThreshold => ExtensionServiceFailureCode.HealthCheckFailed,
        ServiceStateReasonCode.HealthTimeout => ExtensionServiceFailureCode.HealthTimeout,
        ServiceStateReasonCode.ProcessExited => ExtensionServiceFailureCode.ProcessExited,
        ServiceStateReasonCode.RestartAttemptLimitReached => ExtensionServiceFailureCode.RestartLimitReached,
        ServiceStateReasonCode.Cancelled => ExtensionServiceFailureCode.Cancelled,
        ServiceStateReasonCode.DesiredDisabled or
            ServiceStateReasonCode.DesiredStopped or
            ServiceStateReasonCode.None or
            ServiceStateReasonCode.StartRequested or
            ServiceStateReasonCode.StartAccepted or
            ServiceStateReasonCode.HealthPending or
            ServiceStateReasonCode.Healthy or
            ServiceStateReasonCode.StopRequested or
            ServiceStateReasonCode.StopCompleted or
        ServiceStateReasonCode.ProcessExitedSuccessfully or
            ServiceStateReasonCode.DeadlineExpired or
            ServiceStateReasonCode.Superseded => ExtensionServiceFailureCode.None,
        _ => ExtensionServiceFailureCode.Unknown
    };

    private static string? DescribeFailure(ExtensionServiceFailureCode code) => code switch
    {
        ExtensionServiceFailureCode.None => null,
        ExtensionServiceFailureCode.StartRejected => "The process start was rejected.",
        ExtensionServiceFailureCode.InvalidLaunchSpecification => "The process launch specification is invalid.",
        ExtensionServiceFailureCode.MissingHostEnvironment => "A required host environment value is unavailable.",
        ExtensionServiceFailureCode.ExecutableMissing => "The service executable is not available yet.",
        ExtensionServiceFailureCode.DependencyUnavailable => "A required startup dependency is unavailable.",
        ExtensionServiceFailureCode.PortLeaseUnavailable => "The service port lease is unavailable.",
        ExtensionServiceFailureCode.RuntimeUnavailable => "A required runtime resource is unavailable.",
        ExtensionServiceFailureCode.HealthCheckFailed => "The service health check failed.",
        ExtensionServiceFailureCode.HealthTimeout => "The service health check timed out.",
        ExtensionServiceFailureCode.ProcessExited => "The service process exited while it was expected to remain running.",
        ExtensionServiceFailureCode.RestartPolicyDisabled => "The configured restart policy prevents another restart.",
        ExtensionServiceFailureCode.RestartLimitReached => "The configured restart-attempt limit was reached.",
        ExtensionServiceFailureCode.Cancelled => "The service operation was cancelled.",
        _ => "The service operation failed."
    };
}
