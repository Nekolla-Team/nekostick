using System.Collections.Immutable;
using System.Net;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Proxy;

namespace Nekolla.Nekostick.Host;

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
/// <param name="Port">The loopback port assigned to the service.</param>
/// <param name="ExpiresAt">The time at which the lease expires.</param>
/// <param name="OwnerExtensionId">The persisted extension owner, or <see langword="null"/> for Host-owned services.</param>
public sealed record HostServiceEndpointLease(
    Guid ServiceId,
    int Port,
    DateTimeOffset ExpiresAt,
    string? OwnerExtensionId = null)
{
    /// <summary>Determines whether the lease identifies a valid, unexpired endpoint at the specified time.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when the lease is active; otherwise, <see langword="false"/>.</returns>
    public bool IsActive(DateTimeOffset now) =>
        ServiceId != Guid.Empty && Port is >= 1 and <= 65535 && now < ExpiresAt;
}
/// <summary>Atomically publishes a complete endpoint lease snapshot.</summary>
public sealed class HostServiceEndpointSnapshotPublisher : IHostServiceEndpointSnapshotAccessor
{
    private readonly object _gate = new();
    private readonly ExtensionRuntimeManager? _runtimeManager;
    private ImmutableDictionary<Guid, HostServiceEndpointLease> _current =
        ImmutableDictionary<Guid, HostServiceEndpointLease>.Empty;
    private long _publicationVersion;

    /// <summary>Creates an endpoint publisher with optional core-event fan-out.</summary>
    /// <param name="runtimeManager">The extension runtime manager, when core events are enabled.</param>
    public HostServiceEndpointSnapshotPublisher(ExtensionRuntimeManager? runtimeManager = null)
    {
        _runtimeManager = runtimeManager;
    }

    /// <summary>Gets the current immutable endpoint lease view.</summary>
    public ImmutableDictionary<Guid, HostServiceEndpointLease> Current =>
        Volatile.Read(ref _current);

    /// <summary>Replaces the complete lease view; invalid entries are discarded.</summary>
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

        var next = builder.ToImmutable();
        lock (_gate)
        {
            var previous = _current;
            if (SnapshotsEqual(previous, next))
            {
                return;
            }

            Volatile.Write(ref _current, next);
            var version = checked(++_publicationVersion);
            PublishChanges(previous, next, version);
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
                });
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
