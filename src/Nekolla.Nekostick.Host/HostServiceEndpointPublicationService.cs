using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

/// <summary>Publishes the complete local, unexpired service endpoint view without request-time database reads.</summary>
public sealed class HostServiceEndpointPublicationService : BackgroundService
{
    private readonly IDbContextFactory<NekostickDbContext> _dbContextFactory;
    private readonly Func<NekostickDbContext, IPersistencePortLeaseStore> _leaseStoreFactory;
    private readonly IHostServiceEndpointAuthority _authority;
    private readonly HostRuntimeOptions _options;
    private readonly ILogger _logger;
    private readonly HostLogThrottle _publicationThrottle = new();

    /// <summary>Creates the endpoint publication background service.</summary>
    /// <param name="dbContextFactory">The factory for persistence contexts.</param>
    /// <param name="authority">The lifecycle-authoritative endpoint publisher.</param>
    /// <param name="options">The host runtime options containing the node identity.</param>
    /// <param name="logger">The optional host logger for endpoint publication diagnostics.</param>
    public HostServiceEndpointPublicationService(
        IDbContextFactory<NekostickDbContext> dbContextFactory,
        IHostServiceEndpointAuthority authority,
        HostRuntimeOptions options,
        ILogger? logger = null)
        : this(
            dbContextFactory,
            authority,
            options,
            db => new EfPortLeaseStore(db, logger: logger),
            logger)
    {
    }

    internal HostServiceEndpointPublicationService(
        IDbContextFactory<NekostickDbContext> dbContextFactory,
        IHostServiceEndpointAuthority authority,
        HostRuntimeOptions options,
        Func<NekostickDbContext, IPersistencePortLeaseStore> leaseStoreFactory,
        ILogger? logger = null)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _leaseStoreFactory = leaseStoreFactory ?? throw new ArgumentNullException(nameof(leaseStoreFactory));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Publishes endpoint leases initially and at a fixed interval until cancellation.</summary>
    /// <param name="stoppingToken">The service shutdown cancellation token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        await PublishAsync(stoppingToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PublishAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var store = _leaseStoreFactory(db);
            PersistencePortLeaseSnapshotResult snapshot;
            try
            {
                snapshot = await store.ReadActiveAsync(_options.NodeId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (store is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (!snapshot.IsAvailable)
            {
                return;
            }

            var serviceIds = snapshot.Leases
                .Where(lease => lease.ExpiresAt > now)
                .Select(lease => lease.ServiceId)
                .Distinct()
                .ToArray();
            if (serviceIds.Length == 0)
            {
                await _authority.PublishVerifiedEndpointsAsync(Array.Empty<HostServiceEndpointLease>()).ConfigureAwait(false);
                return;
            }

            var owners = await db.Services.AsNoTracking()
                .Where(service => serviceIds.Contains(service.Id))
                .Select(service => new { service.Id, service.OwnerExtensionId })
                .ToDictionaryAsync(service => service.Id, service => service.OwnerExtensionId, cancellationToken)
                .ConfigureAwait(false);
            var leases = new List<HostServiceEndpointLease>(snapshot.Leases.Length);
            foreach (var lease in snapshot.Leases)
            {
                if (lease.ExpiresAt <= now || !owners.TryGetValue(lease.ServiceId, out var ownerExtensionId))
                {
                    continue;
                }

                leases.Add(new HostServiceEndpointLease(
                    lease.ServiceId,
                    lease.GenerationId,
                    lease.Port,
                    lease.ExpiresAt,
                    ownerExtensionId));
            }

            await _authority.PublishVerifiedEndpointsAsync(leases).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Keep the lifecycle-authoritative snapshot unchanged while the database is unavailable.
            if (_publicationThrottle.TryAcquire("endpoint-publication", out var occurrences))
            {
                HostLogMessages.EndpointPublicationFailed(_logger, exception, _options.NodeId, occurrences);
            }
        }
    }
}
