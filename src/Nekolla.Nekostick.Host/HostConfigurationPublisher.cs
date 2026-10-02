using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Persistence;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

/// <summary>Describes the outcome of a configuration publication attempt.</summary>
internal enum PublishOutcome
{
    /// <summary>The snapshot is live and every requested force reload was satisfied.</summary>
    Published,
    /// <summary>The attempt did not complete, but the holder already runs an equal-or-newer committed version.</summary>
    Superseded,
    /// <summary>The publication genuinely failed.</summary>
    Failed
}

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
    private readonly HostRuntimeOptions? _runtimeOptions;
    private readonly HostApiVersion _hostApiVersion;
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
    /// <param name="runtimeOptions">The optional node identity used for node-local extension state reporting.</param>
    /// <param name="hostApiVersion">The host API version used for manifest compatibility validation.</param>
    public HostConfigurationPublisher(
        HostConfigurationSnapshotHolder snapshotHolder,
        ExtensionRuntimeManager runtimeManager,
        HostNodeOptions nodeOptions,
        ILogger<HostConfigurationPublisher> logger,
        IDbContextFactory<NekostickDbContext>? dbContextFactory = null,
        HostRuntimeState? runtimeState = null,
        IHostConfigurationSnapshotReader? snapshotReader = null,
        HostRuntimeOptions? runtimeOptions = null,
        HostApiVersion? hostApiVersion = null)
    {
        _snapshotHolder = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbContextFactory = dbContextFactory;
        _runtimeState = runtimeState;
        _snapshotReader = snapshotReader;
        _runtimeOptions = runtimeOptions;
        _hostApiVersion = hostApiVersion ?? runtimeManager.ApiVersion;
    }

    /// <summary>Attempts to publish a host configuration snapshot.</summary>
    /// <param name="snapshot">The configuration snapshot to publish.</param>
    /// <param name="forceReloadIds">The optional extension identifiers whose runtime instances must be reloaded.</param>
    /// <param name="scheduleRecovery">Whether a failed generation handoff schedules one recovery publication.</param>
    /// <param name="cancellationToken">The publication cancellation token.</param>
    /// <returns>
    /// <see cref="PublishOutcome.Published"/> when publication and requested force reloads complete,
    /// <see cref="PublishOutcome.Superseded"/> when an equal-or-newer committed snapshot already satisfies the goal,
    /// or <see cref="PublishOutcome.Failed"/> when publication genuinely fails.
    /// </returns>
    internal async ValueTask<PublishOutcome> PublishAsync(
        HostConfigurationSnapshot snapshot,
        ImmutableHashSet<string>? forceReloadIds = null,
        bool scheduleRecovery = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requestedForceReloadIds = forceReloadIds ?? EmptyForceReloadIds;
        var outcome = PublishOutcome.Failed;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return outcome = PublishOutcome.Failed;
        }

        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staged = false;
        var stagedSnapshot = snapshot;
        ExtensionGenerationPreparation? activePreparation = null;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return outcome = PublishOutcome.Failed;
            }

            var stageAdmission = _snapshotHolder.TryStage(snapshot);
            if (stageAdmission != SnapshotAdmission.Accepted)
            {
                return outcome = stageAdmission == SnapshotAdmission.Superseded
                    ? PublishOutcome.Superseded
                    : PublishOutcome.Failed;
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
                !desiredSet.HasQuarantinedLoadedRecord &&
                previousGeneration is not null &&
                !HasRunningContentDrift(previousGeneration, desiredSet.Descriptors) &&
                CanReusePriorLoadedIdentities(previousSnapshot!, snapshot))
            {
                var reuseAdmission = _snapshotHolder.TryReplace(snapshot, previousGeneration, serviceOwners);
                if (reuseAdmission != SnapshotAdmission.Accepted)
                {
                    return outcome = reuseAdmission == SnapshotAdmission.Superseded
                        ? PublishOutcome.Superseded
                        : PublishOutcome.Failed;
                }
                HostLogMessages.PriorGenerationReused(
                    _logger,
                    "UnavailableLoadedRecordAndIdentitiesReused",
                    previousGeneration.GenerationId);

                // TryReplace consumed the staged snapshot; it is now the live
                // publication, so staging cleanup and rejection no longer apply.
                staged = false;
                DeliverPublicationEvents(snapshot, previousSnapshot!.Configuration);
                await ReportNodeStatesAsync(desiredSet.NodeStates, cancellationToken).ConfigureAwait(false);
                // Reusing the prior generation cannot satisfy a forced reload;
                // keep the live publication but report the reload as unsuccessful.
                return outcome = requestedForceReloadIds.Count == 0
                    ? PublishOutcome.Published
                    : PublishOutcome.Superseded;
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
                return outcome = PublishOutcome.Failed;
            }

            var preparation = preparedResult.Preparation;
            activePreparation = preparation;
            if (HasUnsafeUnavailableBinding(preparation.Generation))
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                activePreparation = null;
                var fallbackOutcome = await PublishWithPreviousOrEmptyAsync(
                        snapshot,
                        previousGeneration,
                        desiredSet.NodeStates,
                        cancellationToken)
                    .ConfigureAwait(false);
                var unavailableBindings = string.Join(
                    "; ",
                    preparation.Generation.Bindings
                        .Where(static binding => !binding.Available)
                        .Select(static binding => $"{binding.ExtensionId ?? "unknown"}={binding.FailureCode}"));
                HostLogMessages.UnsafeUnavailableBindingFallback(
                    _logger,
                    preparation.Generation.GenerationId,
                    fallbackOutcome != PublishOutcome.Failed,
                    unavailableBindings);
                // Fallback publishes the snapshot without forcing the requested reload;
                // report Superseded when it cannot satisfy the forced reload.
                if (fallbackOutcome == PublishOutcome.Failed)
                {
                    return outcome = PublishOutcome.Failed;
                }

                return outcome = requestedForceReloadIds.Count == 0
                    ? PublishOutcome.Published
                    : PublishOutcome.Superseded;
            }

            var ready = await preparation.ReadyToPublishAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded || ready.Generation is null)
            {
                await preparation.AbortAsync().ConfigureAwait(false);
                activePreparation = null;
                var fallbackOutcome = await PublishWithPreviousOrEmptyAsync(
                        snapshot,
                        previousGeneration,
                        desiredSet.NodeStates,
                        cancellationToken)
                    .ConfigureAwait(false);
                HostLogMessages.GenerationReadyFallback(
                    _logger,
                    ready.FailureCode.ToString(),
                    fallbackOutcome != PublishOutcome.Failed);
                if (scheduleRecovery)
                {
                    ScheduleRecoveryPublication(snapshot);
                }

                if (fallbackOutcome == PublishOutcome.Failed)
                {
                    return outcome = PublishOutcome.Failed;
                }

                return outcome = requestedForceReloadIds.Count == 0
                    ? PublishOutcome.Published
                    : PublishOutcome.Superseded;
            }

            var publicationSnapshot = await ReadLatestSnapshotAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            if (publicationSnapshot is null)
            {
                return outcome = PublishOutcome.Failed;
            }

            var publicationStageAdmission = _snapshotHolder.TryStage(publicationSnapshot);
            if (publicationStageAdmission != SnapshotAdmission.Accepted)
            {
                return outcome = publicationStageAdmission == SnapshotAdmission.Superseded
                    ? PublishOutcome.Superseded
                    : PublishOutcome.Failed;
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
            var publicationAdmission = _snapshotHolder.TryReplace(
                publicationSnapshot,
                ready.Generation,
                publicationServiceOwners);
            if (publicationAdmission != SnapshotAdmission.Accepted)
            {
                return outcome = publicationAdmission == SnapshotAdmission.Superseded
                    ? PublishOutcome.Superseded
                    : PublishOutcome.Failed;
            }

            // TryReplace makes the prepared generation the live publication. It
            // must not be aborted or marked rejected when manager completion or
            // event delivery fails afterwards.
            staged = false;
            activePreparation = null;
            if (!await preparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotCompletionFailed(_logger, publicationSnapshot.Version);
                // TryReplace already made the snapshot live; completion failure
                // does not undo the publication, so the goal is achieved.
                return outcome = PublishOutcome.Superseded;
            }

            HostLogMessages.ConfigurationSnapshotApplied(_logger, publicationSnapshot.Version);
            DeliverPublicationEvents(publicationSnapshot, previousSnapshot?.Configuration);
            await ReportNodeStatesAsync(desiredSet.NodeStates, cancellationToken).ConfigureAwait(false);
            return outcome = PublishOutcome.Published;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(PublishAsync));
            HostLogMessages.ConfigurationSnapshotRejected(_logger, "PublishException");
            return outcome = PublishOutcome.Failed;
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
            catch (Exception exception)
            {
                HostLogMessages.ConfigurationPublicationCleanupFailed(
                    _logger,
                    exception,
                    "GenerationAbort");
                // Abort owns its manager gate cleanup; publication cleanup must
                // still release the publisher gate when lifecycle cleanup fails.
            }

            try
            {
                if (staged)
                {
                    _snapshotHolder.ClearStaged(stagedSnapshot);
                    if (outcome == PublishOutcome.Failed)
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
    /// <summary>Queues a single follow-up publication after a commit failure that stopped previous generations.</summary>
    /// <param name="snapshot">The durable Host configuration snapshot to republish.</param>
    private void ScheduleRecoveryPublication(HostConfigurationSnapshot snapshot)
    {
        // A failed ReadyToPublishAsync handoff may leave previous generations
        // stopped without replacements. Exactly one recovery pass lets a fresh
        // candidate start cleanly; the follow-up disables further scheduling so
        // a deterministically failing extension cannot loop the pipeline.
        HostLogMessages.ConfigurationRecoveryPublicationScheduled(_logger, snapshot.Version);
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await PublishAsync(snapshot, scheduleRecovery: false).ConfigureAwait(false);
                var succeeded = outcome != PublishOutcome.Failed;
                HostLogMessages.ConfigurationRecoveryPublicationCompleted(
                    _logger,
                    succeeded ? LogLevel.Information : LogLevel.Warning,
                    snapshot.Version,
                    succeeded);
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, "RecoveryPublication");
                HostLogMessages.ConfigurationRecoveryPublicationCompleted(
                    _logger,
                    LogLevel.Warning,
                    snapshot.Version,
                    false);
            }
        });
    }

    /// <summary>Publishes a snapshot while forcing one loaded extension through candidate replacement.</summary>
    /// <param name="snapshot">The durable Host configuration snapshot to publish.</param>
    /// <param name="extensionId">The extension identifier that must be reloaded.</param>
    /// <param name="cancellationToken">The publication cancellation token.</param>
    /// <returns>The published result with the committed version when the forced reload completes; otherwise a failure result.</returns>
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

        // Restart the recorded contract consumers in the same generation so they re-import from
        // the reloaded provider; the generation handoff keeps the availability guarantees, and the
        // topological candidate order restarts providers before their dependents.
        var forceReloadIds = EmptyForceReloadIds
            .Add(extensionId)
            .Union(_runtimeManager.GetCascadeReloadSet(extensionId));
        var outcome = await PublishAsync(
                latest,
                forceReloadIds,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return outcome == PublishOutcome.Published
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

    private async ValueTask<PublishOutcome> PublishWithPreviousOrEmptyAsync(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? previousGeneration,
        ImmutableArray<ExtensionNodeStateWrite> nodeStates,
        CancellationToken cancellationToken)
    {
        var publicationSnapshot = await ReadLatestSnapshotAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (publicationSnapshot is null)
        {
            return PublishOutcome.Failed;
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
            var previousReplacementAdmission = _snapshotHolder.TryReplace(
                publicationSnapshot,
                previousGeneration,
                publicationServiceOwners);
            if (previousReplacementAdmission != SnapshotAdmission.Accepted)
            {
                return previousReplacementAdmission == SnapshotAdmission.Superseded
                    ? PublishOutcome.Superseded
                    : PublishOutcome.Failed;
            }
            HostLogMessages.ConfigurationFallbackPublished(
                _logger,
                "Previous",
                publicationSnapshot.Version);

            DeliverPublicationEvents(publicationSnapshot, previousSnapshot);
            await ReportNodeStatesAsync(nodeStates, cancellationToken).ConfigureAwait(false);
            return PublishOutcome.Published;
        }

        var emptyResult = await _runtimeManager
            .PrepareGenerationAsync(
                ImmutableArray<ExtensionRuntimeDescriptor>.Empty,
                null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!emptyResult.Succeeded || emptyResult.Preparation is null)
        {
            return PublishOutcome.Failed;
        }

        var emptyPreparation = emptyResult.Preparation;
        var completed = false;
        try
        {
            var ready = await emptyPreparation.ReadyToPublishAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded || ready.Generation is null)
            {
                return PublishOutcome.Failed;
            }

            var emptyGenerationAdmission = _snapshotHolder.TryReplace(
                publicationSnapshot,
                ready.Generation,
                publicationServiceOwners);
            if (emptyGenerationAdmission != SnapshotAdmission.Accepted)
            {
                return emptyGenerationAdmission == SnapshotAdmission.Superseded
                    ? PublishOutcome.Superseded
                    : PublishOutcome.Failed;
            }

            // TryReplace makes the prepared generation the live publication. It
            // must not be aborted when manager completion or event delivery
            // fails afterwards.
            completed = true;
            if (!await emptyPreparation.CompletePublicationAsync().ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotCompletionFailed(_logger, publicationSnapshot.Version);
                return PublishOutcome.Superseded;
            }
            HostLogMessages.ConfigurationFallbackPublished(
                _logger,
                "Empty",
                publicationSnapshot.Version);

            DeliverPublicationEvents(publicationSnapshot, previousSnapshot);
            await ReportNodeStatesAsync(nodeStates, cancellationToken).ConfigureAwait(false);
            return PublishOutcome.Published;
        }
        finally
        {
            if (!completed)
            {
                await emptyPreparation.AbortAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ReportNodeStatesAsync(
        ImmutableArray<ExtensionNodeStateWrite> nodeStates,
        CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null ||
            _runtimeOptions is null ||
            string.IsNullOrWhiteSpace(_runtimeOptions.NodeId))
        {
            return;
        }

        try
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var persistence = new EfExtensionNodeStatePersistence(db, logger: _logger);
            if (!await persistence
                    .UpsertAsync(_runtimeOptions.NodeId, nodeStates, cancellationToken)
                    .ConfigureAwait(false))
            {
                HostLogMessages.NodeStatePersistenceFailed(_logger, null);
            }
        }
        catch (Exception exception)
        {
            // Node-state telemetry is best effort and must never reject an already live snapshot.
            HostLogMessages.NodeStatePersistenceFailed(_logger, exception);
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
            _runtimeState?.MarkDatabaseUnavailable();
            throw;
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
            _runtimeState?.MarkDatabaseUnavailable();
            throw;
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
            },
            _logger);

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
                },
                _logger);
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
                    },
                    _logger);
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
            extensionId,
            _logger);
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
