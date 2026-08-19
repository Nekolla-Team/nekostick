using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

/// <summary>Maps the Persistence-owned lease boundary to the supervision lease contract.</summary>
public sealed class HostPortLeaseStoreAdapter : IPortLeaseStore
{
    private readonly IPersistencePortLeaseStore? _store;
    private readonly IDbContextFactory<NekostickDbContext>? _dbContextFactory;
    private readonly HostRuntimeState _runtimeState;

    /// <summary>Creates an adapter over a caller-owned Persistence lease implementation.</summary>
    public HostPortLeaseStoreAdapter(IPersistencePortLeaseStore store, HostRuntimeState runtimeState)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
    }

    /// <summary>Creates a singleton-safe adapter that scopes each mutation to one DbContext.</summary>
    public HostPortLeaseStoreAdapter(
        IDbContextFactory<NekostickDbContext> dbContextFactory,
        HostRuntimeState runtimeState)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
    }

    /// <inheritdoc />
    public async ValueTask<PortLeaseOperationResult> ApplyAsync(
        PortLeaseIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        try
        {
            if (_store is not null)
            {
                return await ApplyToStoreAsync(_store, intent, cancellationToken).ConfigureAwait(false);
            }

            await using var db = await _dbContextFactory!
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            return await ApplyToStoreAsync(new EfPortLeaseStore(db), intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PortLeaseOperationResult(PortLeaseOperationStatus.Cancelled);
        }
        catch
        {
            _runtimeState.MarkDatabaseUnavailable();
            return new PortLeaseOperationResult(PortLeaseOperationStatus.DatabaseUnavailable);
        }
    }

    private async ValueTask<PortLeaseOperationResult> ApplyToStoreAsync(
        IPersistencePortLeaseStore store,
        PortLeaseIntent intent,
        CancellationToken cancellationToken)
    {
        var result = intent.Kind switch
        {
            PortLeaseIntentKind.Acquire when intent.Request is not null =>
                await AcquireAsync(store, intent.Request, cancellationToken).ConfigureAwait(false),
            PortLeaseIntentKind.Renew when intent.Renewal is not null =>
                await RenewAsync(store, intent.Renewal, cancellationToken).ConfigureAwait(false),
            PortLeaseIntentKind.Release when intent.Release is not null =>
                await ReleaseAsync(store, intent.Release, cancellationToken).ConfigureAwait(false),
            _ => new PortLeaseOperationResult(PortLeaseOperationStatus.Rejected)
        };
        return result;
    }

    private async ValueTask<PortLeaseOperationResult> AcquireAsync(
        IPersistencePortLeaseStore store,
        PortLeaseRequest request,
        CancellationToken cancellationToken)
    {
        if (!_runtimeState.NewLeasesAllowed)
        {
            return new PortLeaseOperationResult(PortLeaseOperationStatus.DatabaseUnavailable);
        }

        var result = await store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                request.NodeId.Value,
                request.ServiceId,
                request.Port,
                request.TimeToLive,
                request.ExpectedVersion,
                request.AutomaticPortRangeStart,
                request.AutomaticPortRangeEnd),
            cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    private async ValueTask<PortLeaseOperationResult> RenewAsync(
        IPersistencePortLeaseStore store,
        PortLeaseRenewal request,
        CancellationToken cancellationToken)
    {
        if (!_runtimeState.NewLeasesAllowed)
        {
            return new PortLeaseOperationResult(PortLeaseOperationStatus.DatabaseUnavailable);
        }

        var result = await store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                request.NodeId.Value,
                request.ServiceId,
                request.Port,
                request.LeaseVersion,
                request.TimeToLive),
            cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    private async ValueTask<PortLeaseOperationResult> ReleaseAsync(
        IPersistencePortLeaseStore store,
        PortLeaseRelease request,
        CancellationToken cancellationToken)
    {
        var result = await store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                request.NodeId.Value,
                request.ServiceId,
                request.Port,
                request.LeaseVersion),
            cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    private PortLeaseOperationResult Map(PersistencePortLeaseOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == PersistencePortLeaseOperationStatus.DatabaseUnavailable)
        {
            _runtimeState.MarkDatabaseUnavailable();
        }
        else if (result.Status == PersistencePortLeaseOperationStatus.Applied)
        {
            _runtimeState.MarkDatabaseAvailable();
        }

        var status = result.Status switch
        {
            PersistencePortLeaseOperationStatus.Applied => PortLeaseOperationStatus.Applied,
            PersistencePortLeaseOperationStatus.Conflict => PortLeaseOperationStatus.Conflict,
            PersistencePortLeaseOperationStatus.NotFound => PortLeaseOperationStatus.NotFound,
            PersistencePortLeaseOperationStatus.DatabaseUnavailable => PortLeaseOperationStatus.DatabaseUnavailable,
            PersistencePortLeaseOperationStatus.Cancelled => PortLeaseOperationStatus.Cancelled,
            _ => PortLeaseOperationStatus.Rejected
        };
        if (result.Lease is null)
        {
            return new PortLeaseOperationResult(status);
        }

        try
        {
            var lease = result.Lease;
            return new PortLeaseOperationResult(
                status,
                new PortLease(
                    new NodeIdentifier(lease.NodeId),
                    lease.ServiceId,
                    lease.Port,
                    lease.AcquiredAt,
                    lease.ExpiresAt,
                    lease.Version));
        }
        catch
        {
            _runtimeState.MarkDatabaseUnavailable();
            return new PortLeaseOperationResult(PortLeaseOperationStatus.Rejected);
        }
    }
}
