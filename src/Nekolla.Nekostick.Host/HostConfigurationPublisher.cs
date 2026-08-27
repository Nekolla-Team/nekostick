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
    private readonly HostRuntimeState? _runtimeState;
    private readonly IHostConfigurationSnapshotReader? _snapshotReader;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private ImmutableDictionary<Guid, string?> _routeOwners = ImmutableDictionary<Guid, string?>.Empty;
    private int _disposed;

    /// <summary>Creates a configuration publisher for the supplied snapshot and extension runtime state.</summary>
    /// <param name="snapshotHolder">The holder for the currently published host configuration snapshot.</param>
    /// <param name="runtimeManager">The extension runtime manager used to prepare and publish generations.</param>
    /// <param name="nodeOptions">The immutable host node options controlling extension publication.</param>
    /// <param name="logger">The logger used to record publication failures.</param>
    /// <param name="dbContextFactory">The optional persistence factory used to load service ownership metadata.</param>
    /// <param name="runtimeState">The optional runtime capability state updated during staged publication.</param>
    /// <param name="snapshotReader">The optional durable snapshot reader used to reload startup-owned writes before publication.</param>
    public HostConfigurationPublisher(
        HostConfigurationSnapshotHolder snapshotHolder,
        ExtensionRuntimeManager runtimeManager,
        HostNodeOptions nodeOptions,
        ILogger<HostConfigurationPublisher> logger,
        IDbContextFactory<NekostickDbContext>? dbContextFactory = null,
        HostRuntimeState? runtimeState = null,
        IHostConfigurationSnapshotReader? snapshotReader = null)
    {
        _snapshotHolder = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbContextFactory = dbContextFactory;
        _runtimeState = runtimeState;
        _snapshotReader = snapshotReader;
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
        var staged = false;
        var published = false;
        var stagedSnapshot = snapshot;
        ExtensionGenerationPreparation? activePreparation = null;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            if (!_snapshotHolder.TryStage(snapshot))
            {
                return false;
            }

            staged = true;
            _runtimeState?.BeginStagedConfigurationWrites();

            var serviceOwners = await ReadServiceOwnersAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _routeOwners = await ReadRouteOwnersAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var previousSnapshot = _snapshotHolder.RoutingSnapshot;
            var previousGeneration = previousSnapshot?.DispatchGeneration;
            var desiredSet = await BuildDesiredAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (desiredSet.HasUnavailableLoadedRecord &&
                previousGeneration is not null &&
                CanReusePriorLoadedIdentities(previousSnapshot!, snapshot))
            {
                if (!_snapshotHolder.TryReplace(snapshot, previousGeneration, serviceOwners))
                {
                    return false;
                }

                PublishSnapshotEvents(snapshot, previousSnapshot!.Configuration);
                published = true;
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
            activePreparation = preparation;
            if (HasUnsafeUnavailableBinding(preparation.Generation))
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                activePreparation = null;
                var fallbackPublished = await PublishWithPreviousOrEmptyAsync(
                        snapshot,
                        previousGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
                published = fallbackPublished;
                return fallbackPublished;
            }

            var ready = await preparation.ReadyToPublishAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded || ready.Generation is null)
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                activePreparation = null;
                var fallbackPublished = await PublishWithPreviousOrEmptyAsync(
                        snapshot,
                        previousGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
                published = fallbackPublished;
                return fallbackPublished;
            }

            var publicationSnapshot = await ReadLatestSnapshotAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            if (publicationSnapshot is null ||
                !_snapshotHolder.TryStage(publicationSnapshot))
            {
                return false;
            }

            stagedSnapshot = publicationSnapshot;
            var publicationServiceOwners = await ReadServiceOwnersAsync(
                    publicationSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            _routeOwners = await ReadRouteOwnersAsync(
                    publicationSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!_snapshotHolder.TryReplace(
                    publicationSnapshot,
                    ready.Generation,
                    publicationServiceOwners))
            {
                return false;
            }

            if (!await preparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotRejected(_logger);
                return false;
            }

            // CompletePublicationAsync is the irrevocable manager handoff. Do not
            // abort this preparation if later event/log delivery fails.
            activePreparation = null;
            HostLogMessages.ConfigurationSnapshotApplied(_logger, publicationSnapshot.Version);
            PublishSnapshotEvents(publicationSnapshot, previousSnapshot?.Configuration);
            published = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(PublishAsync));
            HostLogMessages.ConfigurationSnapshotRejected(_logger);
            return false;
        }
        finally
        {
            try
            {
                if (activePreparation is not null)
                {
                    await activePreparation.AbortAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Abort owns its manager gate cleanup; publication cleanup must
                // still release the publisher gate when lifecycle cleanup fails.
            }

            try
            {
                if (staged)
                {
                    _snapshotHolder.ClearStaged(stagedSnapshot);
                    if (!published)
                    {
                        _runtimeState?.MarkSnapshotRejected();
                    }
                }

                _runtimeState?.EndStagedConfigurationWrites();
            }
            finally
            {
                _publicationGate.Release();
            }
        }


    }
    private async ValueTask<bool> PublishWithPreviousOrEmptyAsync(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? previousGeneration,
        CancellationToken cancellationToken)
    {
        var publicationSnapshot = await ReadLatestSnapshotAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (publicationSnapshot is null)
        {
            return false;
        }

        var publicationServiceOwners = await ReadServiceOwnersAsync(
                publicationSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
        _routeOwners = await ReadRouteOwnersAsync(
                publicationSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
        var previousSnapshot = _snapshotHolder.Current;
        if (previousGeneration is not null)
        {
            if (!_snapshotHolder.TryReplace(
                    publicationSnapshot,
                    previousGeneration,
                    publicationServiceOwners))
            {
                return false;
            }

            PublishSnapshotEvents(publicationSnapshot, previousSnapshot);
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
        var completed = false;
        try
        {
            var ready = await emptyPreparation.ReadyToPublishAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded || ready.Generation is null ||
                !_snapshotHolder.TryReplace(publicationSnapshot, ready.Generation, publicationServiceOwners))
            {
                return false;
            }

            if (!await emptyPreparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                return false;
            }

            completed = true;
            PublishSnapshotEvents(publicationSnapshot, previousSnapshot);
            return true;
        }
        finally
        {
            if (!completed)
            {
                await emptyPreparation.AbortAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<HostConfigurationSnapshot?> ReadLatestSnapshotAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_snapshotReader is null)
        {
            return snapshot;
        }

        try
        {
            var loaded = await _snapshotReader
                .ReadCompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess || loaded.Value is null || loaded.Value.Version < snapshot.Version)
            {
                return null;
            }

            return loaded.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(ReadLatestSnapshotAsync));
            return null;
        }
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
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(ReadServiceOwnersAsync));
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
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(ReadRouteOwnersAsync));
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
            _runtimeState?.EndStagedConfigurationWrites();
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
