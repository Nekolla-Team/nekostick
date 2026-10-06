using System.Collections.Immutable;
using System.Net;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Proxy;

namespace Nekolla.Nekostick.Host;

/// <summary>Captures one committed dependency generation without exposing mutable lifecycle handles.</summary>
internal sealed record HostServiceCommittedDependencyBinding(
    Guid ServiceId,
    long ServiceVersion,
    Guid GenerationId,
    int Port,
    ImmutableDictionary<string, string> ResolvedEnvironment,
    string? OwnerExtensionId);

/// <summary>Captures one ready service generation in the committed graph view.</summary>
internal sealed record HostServiceCommittedService(
    long ServiceVersion,
    Guid GenerationId,
    HostServiceEndpointLease Endpoint,
    ImmutableDictionary<Guid, HostServiceCommittedDependencyBinding> DependencyBindings,
    ImmutableDictionary<string, string> ResolvedEnvironment,
    HostServiceRuntimeSnapshot Runtime);

/// <summary>One immutable publication of endpoint, dependency, and runtime-query state.</summary>
internal sealed record HostServiceCommittedGraphView(
    long ConfigurationVersion,
    ImmutableDictionary<Guid, HostServiceCommittedService> Services,
    ImmutableDictionary<Guid, HostServiceEndpointLease> Endpoints,
    ImmutableDictionary<Guid, HostServiceRuntimeSnapshot> RuntimeSnapshots)
{
    internal static HostServiceCommittedGraphView Empty { get; } = new(
        0,
        ImmutableDictionary<Guid, HostServiceCommittedService>.Empty,
        ImmutableDictionary<Guid, HostServiceEndpointLease>.Empty,
        ImmutableDictionary<Guid, HostServiceRuntimeSnapshot>.Empty);
}

/// <summary>Provides the immutable, already-published local endpoint lease view.</summary>
public interface IHostServiceEndpointSnapshotAccessor
{
    /// <summary>Gets the atomically published endpoint leases.</summary>
    ImmutableDictionary<Guid, HostServiceEndpointLease> Current { get; }
}
/// <summary>Publishes database-verified endpoint leases that match lifecycle-ready generations.</summary>
public interface IHostServiceEndpointAuthority
{
    /// <summary>Publishes the database leases that match the current active-ready lifecycle identities.</summary>
    /// <param name="dbLeases">The node-local, owner-joined, unexpired leases read from the database.</param>
    /// <returns>A task that completes after the verified leases are published.</returns>
    Task PublishVerifiedEndpointsAsync(IReadOnlyList<HostServiceEndpointLease> dbLeases);
}

/// <summary>Safe endpoint lease data published by Host lifecycle composition.</summary>
/// <param name="ServiceId">The service identifier associated with the lease.</param>
/// <param name="GenerationId">The UUID v7 service generation identifier.</param>
/// <param name="Port">The loopback port assigned to the service.</param>
/// <param name="ExpiresAt">The time at which the lease expires.</param>
/// <param name="OwnerExtensionId">The persisted extension owner, or <see langword="null"/> for Host-owned services.</param>
public sealed record HostServiceEndpointLease(
    Guid ServiceId,
    Guid GenerationId,
    int Port,
    DateTimeOffset ExpiresAt,
    string? OwnerExtensionId = null)
{
    /// <summary>Determines whether the lease identifies a valid, unexpired endpoint at the specified time.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when the lease is active; otherwise, <see langword="false"/>.</returns>
    public bool IsActive(DateTimeOffset now) =>
        ServiceId != Guid.Empty &&
        UuidV7.IsVersion7(GenerationId) &&
        Port is >= 1 and <= 65535 &&
        now < ExpiresAt;
}
/// <summary>Atomically publishes a complete endpoint lease snapshot.</summary>
public sealed class HostServiceEndpointSnapshotPublisher : IHostServiceEndpointSnapshotAccessor
{
    private readonly object _gate = new();
    private readonly ExtensionRuntimeManager? _runtimeManager;
    private readonly ILogger? _logger;
    private HostServiceCommittedGraphView _committedView = HostServiceCommittedGraphView.Empty;
    private long _publicationVersion;

    /// <summary>Creates an endpoint publisher with optional core-event fan-out.</summary>
    /// <param name="runtimeManager">The extension runtime manager, when core events are enabled.</param>
    /// <param name="logger">The optional host logger for core-event delivery diagnostics.</param>
    public HostServiceEndpointSnapshotPublisher(
        ExtensionRuntimeManager? runtimeManager = null,
        ILogger? logger = null)
    {
        _runtimeManager = runtimeManager;
        _logger = logger;
    }

    /// <summary>Gets the current immutable endpoint lease view.</summary>
    public ImmutableDictionary<Guid, HostServiceEndpointLease> Current => CommittedView.Endpoints;

    /// <summary>Gets the endpoint, binding, and runtime state published by one lifecycle commit.</summary>
    internal HostServiceCommittedGraphView CommittedView => Volatile.Read(ref _committedView);

    /// <summary>Replaces the complete endpoint-only view; invalid entries are discarded.</summary>
    /// <param name="leases">The leases to validate and publish.</param>
    public void Publish(IEnumerable<HostServiceEndpointLease> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        var builder = ImmutableDictionary.CreateBuilder<Guid, HostServiceEndpointLease>();
        foreach (var lease in leases)
        {
            if (lease is not null && lease.IsActive(DateTimeOffset.UtcNow))
            {
                builder[lease.ServiceId] = lease;
            }
        }

        var nextEndpoints = builder.ToImmutable();
        lock (_gate)
        {
            var previous = _committedView;
            if (SnapshotsEqual(previous.Endpoints, nextEndpoints))
            {
                return;
            }

            var services = previous.Services.ToBuilder();
            foreach (var service in previous.Services)
            {
                if (nextEndpoints.TryGetValue(service.Key, out var endpoint) &&
                    endpoint.GenerationId == service.Value.GenerationId)
                {
                    services[service.Key] = service.Value with { Endpoint = endpoint };
                }
                else
                {
                    services.Remove(service.Key);
                }
            }

            var next = previous with { Services = services.ToImmutable(), Endpoints = nextEndpoints };
            Volatile.Write(ref _committedView, next);
            var version = checked(++_publicationVersion);
            PublishChanges(previous.Endpoints, next.Endpoints, version);
        }
    }

    /// <summary>Atomically replaces lifecycle-owned endpoints, bindings, and runtime snapshots.</summary>
    /// <param name="view">The complete immutable committed graph.</param>
    internal void CommitGraph(HostServiceCommittedGraphView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            var previous = _committedView;
            if (ReferenceEquals(previous, view) || CommittedViewsEqual(previous, view))
            {
                return;
            }

            Volatile.Write(ref _committedView, view);
            if (!SnapshotsEqual(previous.Endpoints, view.Endpoints))
            {
                var version = checked(++_publicationVersion);
                PublishChanges(previous.Endpoints, view.Endpoints, version);
            }
        }
    }

    private static bool CommittedViewsEqual(
        HostServiceCommittedGraphView left,
        HostServiceCommittedGraphView right) =>
        left.ConfigurationVersion == right.ConfigurationVersion &&
        SnapshotsEqual(left.Endpoints, right.Endpoints) &&
        CommittedServicesEqual(left.Services, right.Services) &&
        RuntimeSnapshotsEqual(left.RuntimeSnapshots, right.RuntimeSnapshots);

    private static bool CommittedServicesEqual(
        ImmutableDictionary<Guid, HostServiceCommittedService> left,
        ImmutableDictionary<Guid, HostServiceCommittedService> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (serviceId, service) in left)
        {
            if (!right.TryGetValue(serviceId, out var other) ||
                service.ServiceVersion != other.ServiceVersion ||
                service.GenerationId != other.GenerationId ||
                service.Endpoint != other.Endpoint ||
                !DependencyBindingsEqual(service.DependencyBindings, other.DependencyBindings) ||
                !StringDictionariesEqual(service.ResolvedEnvironment, other.ResolvedEnvironment) ||
                service.Runtime != other.Runtime)
            {
                return false;
            }
        }

        return true;
    }

    private static bool DependencyBindingsEqual(
        ImmutableDictionary<Guid, HostServiceCommittedDependencyBinding> left,
        ImmutableDictionary<Guid, HostServiceCommittedDependencyBinding> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (serviceId, binding) in left)
        {
            if (!right.TryGetValue(serviceId, out var other) ||
                binding.ServiceId != other.ServiceId ||
                binding.ServiceVersion != other.ServiceVersion ||
                binding.GenerationId != other.GenerationId ||
                binding.Port != other.Port ||
                !StringDictionariesEqual(binding.ResolvedEnvironment, other.ResolvedEnvironment) ||
                !string.Equals(binding.OwnerExtensionId, other.OwnerExtensionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StringDictionariesEqual(
        ImmutableDictionary<string, string> left,
        ImmutableDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RuntimeSnapshotsEqual(
        ImmutableDictionary<Guid, HostServiceRuntimeSnapshot> left,
        ImmutableDictionary<Guid, HostServiceRuntimeSnapshot> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (serviceId, snapshot) in left)
        {
            if (!right.TryGetValue(serviceId, out var other) || snapshot != other)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Updates runtime telemetry within the same immutable committed view.</summary>
    /// <param name="snapshots">The latest runtime snapshots from the observability projection.</param>
    internal void SetRuntimeSnapshots(ImmutableArray<HostServiceRuntimeSnapshot> snapshots)
    {
        lock (_gate)
        {
            var current = _committedView;
            var runtime = ImmutableDictionary.CreateBuilder<Guid, HostServiceRuntimeSnapshot>();
            foreach (var snapshot in snapshots)
            {
                if (current.Services.TryGetValue(snapshot.ServiceId, out var service))
                {
                    if (snapshot.GenerationId == service.GenerationId)
                    {
                        runtime[snapshot.ServiceId] = snapshot;
                    }
                    else
                    {
                        runtime[snapshot.ServiceId] = service.Runtime;
                    }
                }
                else
                {
                    runtime[snapshot.ServiceId] = snapshot;
                }
            }

            foreach (var service in current.Services)
            {
                runtime.TryAdd(service.Key, service.Value.Runtime);
            }

            var runtimeSnapshots = runtime.ToImmutable();
            var services = current.Services.ToBuilder();
            foreach (var service in current.Services)
            {
                if (runtimeSnapshots.TryGetValue(service.Key, out var snapshot) &&
                    !ReferenceEquals(snapshot, service.Value.Runtime))
                {
                    services[service.Key] = service.Value with { Runtime = snapshot };
                }
            }

            var view = current with
            {
                Services = services.ToImmutable(),
                RuntimeSnapshots = runtimeSnapshots
            };
            if (!CommittedViewsEqual(current, view))
            {
                Volatile.Write(ref _committedView, view);
            }
        }
    }

    private void PublishChanges(
        ImmutableDictionary<Guid, HostServiceEndpointLease> previous,
        ImmutableDictionary<Guid, HostServiceEndpointLease> next,
        long version)
    {
        var serviceIds = previous.Keys.Concat(next.Keys).ToHashSet();
        foreach (var serviceId in serviceIds)
        {
            var hasPrevious = previous.TryGetValue(serviceId, out var oldLease);
            var hasNext = next.TryGetValue(serviceId, out var newLease);
            if (hasPrevious && hasNext && oldLease == newLease)
            {
                continue;
            }

            HostCoreEventPublisher.Publish(
                _runtimeManager,
                ExtensionCoreEventKind.PortLeaseChanged,
                new
                {
                    serviceId,
                    version,
                    state = hasNext ? hasPrevious ? "changed" : "published" : "withdrawn",
                    port = hasNext ? newLease!.Port : (int?)null
                },
                _logger);
        }
    }

    private static bool SnapshotsEqual(
        ImmutableDictionary<Guid, HostServiceEndpointLease> left,
        ImmutableDictionary<Guid, HostServiceEndpointLease> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var lease) || lease != pair.Value)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Resolves only active, non-expired local loopback leases from one immutable view.</summary>
public sealed class HostServiceEndpointResolver : IMicroserviceEndpointResolver
{
    private readonly IHostServiceEndpointSnapshotAccessor _accessor;

    /// <summary>Creates a resolver over the supplied endpoint snapshot accessor.</summary>
    /// <param name="accessor">The immutable endpoint snapshot accessor.</param>
    public HostServiceEndpointResolver(IHostServiceEndpointSnapshotAccessor accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    /// <summary>Resolves an active service lease to a local loopback endpoint.</summary>
    /// <param name="serviceId">The service identifier to resolve.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The available loopback endpoint, or an unavailable result.</returns>
    public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (serviceId == Guid.Empty ||
            !_accessor.Current.TryGetValue(serviceId, out var lease) ||
            !lease.IsActive(DateTimeOffset.UtcNow))
        {
            return ValueTask.FromResult(MicroserviceEndpointResolution.Unavailable);
        }
        try
        {
            var endpoint = new MicroserviceEndpoint(
                new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), lease.Port).Uri);
            return ValueTask.FromResult(MicroserviceEndpointResolution.Available(endpoint));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(MicroserviceEndpointResolution.Unavailable);
        }
    }
}
