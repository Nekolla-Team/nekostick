using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Persistence;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

/// <summary>Serializes configuration publication with staged extension generation handoff.</summary>
public sealed partial class HostConfigurationPublisher : IAsyncDisposable
{
    private readonly HostConfigurationSnapshotHolder _snapshotHolder;
    private readonly ExtensionRuntimeManager _runtimeManager;
    private readonly HostNodeOptions _nodeOptions;
    private readonly ILogger<HostConfigurationPublisher> _logger;
    private readonly IDbContextFactory<NekostickDbContext>? _dbContextFactory;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private ImmutableDictionary<Guid, string?> _routeOwners = ImmutableDictionary<Guid, string?>.Empty;
    private int _disposed;

    /// <summary>Creates a configuration publisher for the supplied snapshot and extension runtime state.</summary>
    /// <param name="snapshotHolder">The holder for the currently published host configuration snapshot.</param>
    /// <param name="runtimeManager">The extension runtime manager used to prepare and publish generations.</param>
    /// <param name="nodeOptions">The immutable host node options controlling extension publication.</param>
    /// <param name="logger">The logger used to record publication failures.</param>
    /// <param name="dbContextFactory">The optional persistence factory used to load service ownership metadata.</param>
    public HostConfigurationPublisher(
        HostConfigurationSnapshotHolder snapshotHolder,
        ExtensionRuntimeManager runtimeManager,
        HostNodeOptions nodeOptions,
        ILogger<HostConfigurationPublisher> logger,
        IDbContextFactory<NekostickDbContext>? dbContextFactory = null)
    {
        _snapshotHolder = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbContextFactory = dbContextFactory;
    }

    internal async ValueTask<bool> PublishAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var serviceOwners = await ReadServiceOwnersAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _routeOwners = await ReadRouteOwnersAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var previousSnapshot = _snapshotHolder.RoutingSnapshot;
            var previousGeneration = previousSnapshot?.DispatchGeneration;
            var desiredSet = BuildDesired(snapshot);
            if (desiredSet.HasUnavailableLoadedRecord &&
                previousGeneration is not null &&
                CanReusePriorLoadedIdentities(previousSnapshot!, snapshot))
            {
                if (!_snapshotHolder.TryReplace(snapshot, previousGeneration, serviceOwners))
                {
                    return false;
                }

                PublishSnapshotEvents(snapshot, previousSnapshot!.Configuration);
                return true;
            }

            var desired = desiredSet.Descriptors;
            var preparedResult = await _runtimeManager
                .PrepareGenerationAsync(desired, previousGeneration, cancellationToken)
                .ConfigureAwait(false);
            if (!preparedResult.Succeeded || preparedResult.Preparation is null)
            {
                return false;
            }

            var preparation = preparedResult.Preparation;
            if (HasUnsafeUnavailableBinding(preparation.Generation))
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                return await PublishWithPreviousOrEmptyAsync(
                        snapshot,
                        previousGeneration,
                        serviceOwners,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var ready = await preparation.ReadyToPublishAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded || ready.Generation is null)
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                return await PublishWithPreviousOrEmptyAsync(
                        snapshot,
                        previousGeneration,
                        serviceOwners,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!_snapshotHolder.TryReplace(snapshot, ready.Generation, serviceOwners))
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                return false;
            }

            if (!await preparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotRejected(_logger);
                return false;
            }

            PublishSnapshotEvents(snapshot, previousSnapshot?.Configuration);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            HostLogMessages.ConfigurationSnapshotRejected(_logger);
            return false;
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private async ValueTask<bool> PublishWithPreviousOrEmptyAsync(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? previousGeneration,
        ImmutableDictionary<Guid, string?> serviceOwners,
        CancellationToken cancellationToken)
    {
        var previousSnapshot = _snapshotHolder.Current;
        if (previousGeneration is not null)
        {
            if (!_snapshotHolder.TryReplace(snapshot, previousGeneration, serviceOwners))
            {
                return false;
            }

            PublishSnapshotEvents(snapshot, previousSnapshot);
            return true;
        }

        var emptyResult = await _runtimeManager
            .PrepareGenerationAsync(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, null, cancellationToken)
            .ConfigureAwait(false);
        if (!emptyResult.Succeeded || emptyResult.Preparation is null)
        {
            return false;
        }

        var emptyPreparation = emptyResult.Preparation;
        var ready = await emptyPreparation.ReadyToPublishAsync(cancellationToken).ConfigureAwait(false);
        if (!ready.Succeeded || ready.Generation is null ||
            !_snapshotHolder.TryReplace(snapshot, ready.Generation, serviceOwners))
        {
            return false;
        }

        if (!await emptyPreparation.CompletePublicationAsync().ConfigureAwait(false))
        {
            return false;
        }

        PublishSnapshotEvents(snapshot, previousSnapshot);
        return true;
    }

    private async ValueTask<ImmutableDictionary<Guid, string?>> ReadServiceOwnersAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null)
        {
            return snapshot.Services.ToImmutableDictionary(
                static value => value.Id,
                static _ => (string?)null);
        }

        try
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var serviceIds = snapshot.Services.Select(static value => value.Id).ToArray();
            var rows = await db.Services
                .AsNoTracking()
                .Where(value => serviceIds.Contains(value.Id))
                .Select(value => new { value.Id, value.OwnerExtensionId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return rows.ToImmutableDictionary(
                static value => value.Id,
                static value => value.OwnerExtensionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ImmutableDictionary<Guid, string?>.Empty;
        }
    }
    private async ValueTask<ImmutableDictionary<Guid, string?>> ReadRouteOwnersAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null)
        {
            return ImmutableDictionary<Guid, string?>.Empty;
        }

        try
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var routeIds = snapshot.Routes.Select(static value => value.Id).ToArray();
            var rows = await db.Routes
                .AsNoTracking()
                .Where(value => routeIds.Contains(value.Id))
                .Select(value => new { value.Id, value.OwnerExtensionId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return rows.ToImmutableDictionary(
                static value => value.Id,
                static value => value.OwnerExtensionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ImmutableDictionary<Guid, string?>.Empty;
        }
    }

    private void PublishSnapshotEvents(
        HostConfigurationSnapshot snapshot,
        HostConfigurationSnapshot? previous)
    {
        HostCoreEventPublisher.Publish(
            _runtimeManager,
            ExtensionCoreEventKind.ConfigurationSnapshotApplied,
            new
            {
                version = snapshot.Version,
                state = "applied"
            });

        var previousRoutes = previous?.Routes
            .ToDictionary(static route => route.Id);
        var currentIds = new HashSet<Guid>();
        foreach (var route in snapshot.Routes)
        {
            currentIds.Add(route.Id);
            var state = previousRoutes is null ||
                !previousRoutes.TryGetValue(route.Id, out var prior)
                ? "added"
                : route.Version == prior.Version ? null : "changed";
            if (state is null)
            {
                continue;
            }

            HostCoreEventPublisher.Publish(
                _runtimeManager,
                ExtensionCoreEventKind.RouteChanged,
                new
                {
                    routeId = route.Id,
                    version = snapshot.Version,
                    state
                });
        }

        if (previousRoutes is null)
        {
            return;
        }

        foreach (var routeId in previousRoutes.Keys)
        {
            if (currentIds.Contains(routeId))
            {
                continue;
            }

            HostCoreEventPublisher.Publish(
                _runtimeManager,
                ExtensionCoreEventKind.RouteChanged,
                new
                {
                    routeId,
                    version = snapshot.Version,
                    state = "removed"
                });
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _publicationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _snapshotHolder.DisposeAsync().ConfigureAwait(false);
            await _runtimeManager.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _publicationGate.Release();
            _publicationGate.Dispose();
        }
    }
}
