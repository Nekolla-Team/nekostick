using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

/// <summary>Publishes the complete local, unexpired service endpoint view without request-time database reads.</summary>
public sealed class HostServiceEndpointPublicationService : BackgroundService
{
    private readonly IDbContextFactory<NekostickDbContext> _dbContextFactory;
    private readonly HostServiceEndpointSnapshotPublisher _publisher;
    private readonly HostRuntimeOptions _options;

    /// <summary>Creates the endpoint publication background service.</summary>
    /// <param name="dbContextFactory">The factory for persistence contexts.</param>
    /// <param name="publisher">The endpoint snapshot publisher.</param>
    /// <param name="options">The host runtime options containing the node identity.</param>
    public HostServiceEndpointPublicationService(
        IDbContextFactory<NekostickDbContext> dbContextFactory,
        HostServiceEndpointSnapshotPublisher publisher,
        HostRuntimeOptions options)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
            var leases = await db.PortLeases
                .AsNoTracking()
                .Where(value => value.NodeId == _options.NodeId && value.LeaseExpiresAt > now)
                .Select(value => new HostServiceEndpointLease(value.ServiceId, value.Port, value.LeaseExpiresAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _publisher.Publish(leases);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Database loss must withdraw endpoints and keep matched proxy requests fail-closed.
            _publisher.Publish(Array.Empty<HostServiceEndpointLease>());
        }
    }
}
