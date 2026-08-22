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
        ExtensionServiceHealthState healthState)
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


/// <summary>Publishes active supervisor state through the narrow telemetry accessor.</summary>
public sealed partial class HostServiceLifecycleManager : IHostServiceRuntimeSnapshotAccessor
{
    /// <inheritdoc />
    public ImmutableArray<HostServiceRuntimeSnapshot> ReadCurrent()
    {
        var builder = ImmutableArray.CreateBuilder<HostServiceRuntimeSnapshot>();
        foreach (var pair in _slots)
        {
            lock (pair.Value.Gate)
            {
                if (pair.Value.Active is { } generation)
                {
                    builder.Add(CreateRuntimeSnapshot(generation));
                }
            }
        }

        return builder.MoveToImmutable();
    }

    /// <inheritdoc />
    public bool TryGet(Guid serviceId, out HostServiceRuntimeSnapshot snapshot)
    {
        snapshot = null!;
        if (!_slots.TryGetValue(serviceId, out var slot))
        {
            return false;
        }

        lock (slot.Gate)
        {
            if (slot.Active is not { } generation)
            {
                return false;
            }

            snapshot = CreateRuntimeSnapshot(generation);
            return true;
        }
    }

    private static HostServiceRuntimeSnapshot CreateRuntimeSnapshot(ServiceGeneration generation)
    {
        var supervisor = generation.Supervisor;
        var current = supervisor.Snapshot;
        var health = current.Health switch
        {
            ServiceHealthState.Healthy => ExtensionServiceHealthState.Healthy,
            ServiceHealthState.Unhealthy => ExtensionServiceHealthState.Unhealthy,
            _ => ExtensionServiceHealthState.Unknown
        };
        var lifecycle = current.ObservedLifecycle switch
        {
            ServiceLifecycleState.Disabled => ExtensionServiceLifecycleState.Disabled,
            ServiceLifecycleState.Starting => ExtensionServiceLifecycleState.Starting,
            ServiceLifecycleState.Running => ExtensionServiceLifecycleState.Running,
            ServiceLifecycleState.Stopping => ExtensionServiceLifecycleState.Stopping,
            ServiceLifecycleState.Failed => ExtensionServiceLifecycleState.Failed,
            _ => ExtensionServiceLifecycleState.Unknown
        };
        var hasProcess = supervisor.TryGetActiveProcessTelemetry(out var processInstanceId, out var processId, out var startedAt);
        if (!hasProcess && (lifecycle is ExtensionServiceLifecycleState.Starting or ExtensionServiceLifecycleState.Running))
        {
            lifecycle = ExtensionServiceLifecycleState.Unknown;
            health = ExtensionServiceHealthState.Unknown;
        }

        var lastHealthAt = current.LastHealthObservation?.ObservedAt;
        var lastUpdatedAt = current.ChangedAt;
        if (startedAt is { } started && started > lastUpdatedAt)
        {
            lastUpdatedAt = started;
        }

        if (lastHealthAt is { } healthAt && healthAt > lastUpdatedAt)
        {
            lastUpdatedAt = healthAt;
        }

        return new HostServiceRuntimeSnapshot(
            current.ServiceId,
            generation.SnapshotVersion,
            processId,
            processInstanceId,
            startedAt,
            lastUpdatedAt,
            lastHealthAt,
            lifecycle,
            health);
    }
}
