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
        ImmutableHashSet<string>? forceReloadIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requestedForceReloadIds = forceReloadIds ?? EmptyForceReloadIds;
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
            var desiredSet = await BuildDesiredAsync(
                    snapshot,
                    cancellationToken,
                    forceReloadIds: requestedForceReloadIds)
                .ConfigureAwait(false);
            if (desiredSet.HasUnavailableLoadedRecord &&
                previousGeneration is not null &&
                CanReusePriorLoadedIdentities(previousSnapshot!, snapshot))
            {
                if (!_snapshotHolder.TryReplace(snapshot, previousGeneration, serviceOwners))
                {
                    return false;
                }

                // TryReplace consumed the staged snapshot; it is now the live
                // publication, so staging cleanup and rejection no longer apply.
                staged = false;
                DeliverPublicationEvents(snapshot, previousSnapshot!.Configuration);
                published = true;
                // Reusing the prior generation cannot satisfy a forced reload;
                // keep the live publication but report the reload as unsuccessful.
                return requestedForceReloadIds.Count == 0;
            }

            var desired = desiredSet.Descriptors;
            var preparedResult = await _runtimeManager
                .PrepareGenerationAsync(
                    desired,
                    previousGeneration,
                    desiredSet.ForceReloadIds,
                    cancellationToken)
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
                // Fallback publishes the snapshot without forcing the requested
                // reload; preserve publication cleanup while reporting failure.
                published = fallbackPublished;
                return requestedForceReloadIds.Count == 0 && fallbackPublished;
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
                return requestedForceReloadIds.Count == 0 && fallbackPublished;
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

            // TryReplace makes the prepared generation the live publication. It
            // must not be aborted or marked rejected when manager completion or
            // event delivery fails afterwards.
            staged = false;
            activePreparation = null;
            if (!await preparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotCompletionFailed(_logger, publicationSnapshot.Version);
                return false;
            }

            HostLogMessages.ConfigurationSnapshotApplied(_logger, publicationSnapshot.Version);
            DeliverPublicationEvents(publicationSnapshot, previousSnapshot?.Configuration);
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
                    if (!published && !ReferenceEquals(_snapshotHolder.Current, stagedSnapshot))
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
    /// <summary>Publishes a snapshot while forcing one loaded extension through candidate replacement.</summary>
    /// <param name="snapshot">The durable Host configuration snapshot to publish.</param>
    /// <param name="extensionId">The extension identifier that must be reloaded.</param>
    /// <param name="cancellationToken">The publication cancellation token.</param>
    /// <returns>The publication outcome plus the committed snapshot version when accepted.</returns>
    internal async ValueTask<ExtensionReloadPublication> RequestExtensionReloadAsync(
        HostConfigurationSnapshot snapshot,
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return ExtensionReloadPublication.Failed;
        }

        var latest = await ReadLatestSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return ExtensionReloadPublication.Failed;
        }

        // Revalidate against the latest durable snapshot: a concurrent disable/delete may have
        // removed the target after the caller validated its own stale snapshot.
        var target = latest.ExtensionRecords.FirstOrDefault(value =>
            string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal));
        if (target is null || target.LoadState != ExtensionLoadState.Loaded)
        {
            return ExtensionReloadPublication.TargetUnavailable;
        }

        var published = await PublishAsync(
                latest,
                EmptyForceReloadIds.Add(extensionId),
                cancellationToken)
            .ConfigureAwait(false);
        return published
            ? new ExtensionReloadPublication(ExtensionReloadPublicationStatus.Published, latest.Version)
            : ExtensionReloadPublication.Failed;
    }

    /// <summary>Describes one forced extension reload publication.</summary>
    /// <param name="Status">The publication outcome.</param>
    /// <param name="CommittedVersion">The committed snapshot version when published; otherwise zero.</param>
    internal readonly record struct ExtensionReloadPublication(
        ExtensionReloadPublicationStatus Status,
        long CommittedVersion)
    {
        /// <summary>Gets the generic publication failure result.</summary>
        internal static ExtensionReloadPublication Failed =>
            new(ExtensionReloadPublicationStatus.Failed, 0);

        /// <summary>Gets the result for a target that is missing or no longer loaded in the latest snapshot.</summary>
        internal static ExtensionReloadPublication TargetUnavailable =>
            new(ExtensionReloadPublicationStatus.TargetUnavailable, 0);
    }

    /// <summary>Identifies forced reload publication outcomes.</summary>
    internal enum ExtensionReloadPublicationStatus
    {
        /// <summary>The publication failed or reused the prior generation without reloading.</summary>
        Failed,
        /// <summary>The target extension is missing or no longer loaded in the latest durable snapshot.</summary>
        TargetUnavailable,
        /// <summary>The forced publication was accepted.</summary>
        Published
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

            DeliverPublicationEvents(publicationSnapshot, previousSnapshot);
            return true;
        }

        var emptyResult = await _runtimeManager
            .PrepareGenerationAsync(
                ImmutableArray<ExtensionRuntimeDescriptor>.Empty,
                null,
                cancellationToken: cancellationToken)
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

            // TryReplace makes the prepared generation the live publication. It
            // must not be aborted when manager completion or event delivery
            // fails afterwards.
            completed = true;
            if (!await emptyPreparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotCompletionFailed(_logger, publicationSnapshot.Version);
                return false;
            }

            DeliverPublicationEvents(publicationSnapshot, previousSnapshot);
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

    private void DeliverPublicationEvents(
        HostConfigurationSnapshot snapshot,
        HostConfigurationSnapshot? previous)
    {
        try
        {
            PublishSnapshotEvents(snapshot, previous);
        }
        catch (Exception exception)
        {
            // Event delivery happens after the snapshot is already live; a
            // listener failure must not misreport the publication as rejected.
            HostLogMessages.FailureDetails(_logger, exception, nameof(DeliverPublicationEvents));
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

        if (previousRoutes is not null)
        {
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

        PublishExtensionSettingsEvents(snapshot, previous);
    }

    private void PublishExtensionSettingsEvents(
        HostConfigurationSnapshot snapshot,
        HostConfigurationSnapshot? previous)
    {
        var currentSettings = snapshot.ExtensionSettings.ToDictionary(
            static value => value.ExtensionId,
            StringComparer.Ordinal);
        var previousSettings = previous?.ExtensionSettings.ToDictionary(
            static value => value.ExtensionId,
            StringComparer.Ordinal);

        foreach (var current in currentSettings)
        {
            if (previousSettings is not null &&
                previousSettings.TryGetValue(current.Key, out var prior) &&
                prior.Version == current.Value.Version &&
                string.Equals(prior.SettingsJson, current.Value.SettingsJson, StringComparison.Ordinal))
            {
                continue;
            }

            PublishExtensionSettingsChanged(current.Key);
        }

        if (previousSettings is null)
        {
            return;
        }

        foreach (var prior in previousSettings)
        {
            if (!currentSettings.ContainsKey(prior.Key))
            {
                PublishExtensionSettingsChanged(prior.Key);
            }
        }
    }

    private void PublishExtensionSettingsChanged(string extensionId)
    {
        HostCoreEventPublisher.Publish(
            _runtimeManager,
            ExtensionCoreEventKind.ExtensionSettingsChanged,
            new { extensionId },
            extensionId);
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
