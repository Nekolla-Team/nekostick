using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Persistence;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

/// <summary>Serializes configuration publication with staged extension generation handoff.</summary>
public sealed class HostConfigurationPublisher : IAsyncDisposable
{
    private readonly HostConfigurationSnapshotHolder _snapshotHolder;
    private readonly ExtensionRuntimeManager _runtimeManager;
    private readonly HostNodeOptions _nodeOptions;
    private readonly ILogger<HostConfigurationPublisher> _logger;
    private readonly IDbContextFactory<NekostickDbContext>? _dbContextFactory;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
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

    private readonly record struct DesiredExtensionSet(
        ImmutableArray<ExtensionRuntimeDescriptor> Descriptors,
        bool HasUnavailableLoadedRecord);

    private readonly record struct ExtensionSettingsIdentity(
        int SchemaVersion,
        string SettingsJson,
        long Version);

    private readonly record struct LoadedExtensionIdentity(
        string Version,
        long RecordVersion,
        ExtensionSettingsIdentity? Settings);

    private DesiredExtensionSet BuildDesired(HostConfigurationSnapshot snapshot)
    {
        if (_nodeOptions.SkipExtensions)
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, false);
        }

        var loadedRecords = snapshot.ExtensionRecords
            .Where(static record => record is not null && record.LoadState == ExtensionLoadState.Loaded)
            .GroupBy(static record => record.ExtensionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var installRoot = Path.Combine(AppContext.BaseDirectory, "extensions");
        if (!Directory.Exists(installRoot))
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, loadedRecords.Count != 0);
        }

        var discoveredById = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        string[] directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(installRoot)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, loadedRecords.Count != 0);
        }

        foreach (var directory in directories)
        {
            ManifestDiscoveryResult result;
            try
            {
                result = ExtensionManifestDiscovery.Discover(directory);
            }
            catch
            {
                continue;
            }

            if (!result.Succeeded || result.Manifest is null)
            {
                continue;
            }

            var manifest = result.Manifest;
            if (duplicateIds.Contains(manifest.Id))
            {
                continue;
            }

            if (!discoveredById.TryAdd(manifest.Id, manifest))
            {
                discoveredById.Remove(manifest.Id);
                duplicateIds.Add(manifest.Id);
            }
        }

        var localFailure = duplicateIds.Any(loadedRecords.ContainsKey) ||
            loadedRecords.Any(pair => pair.Value.Length != 1 ||
                !discoveredById.TryGetValue(pair.Key, out var manifest) ||
                !string.Equals(pair.Value[0].Version, manifest.Version.ToString(), StringComparison.Ordinal));
        if (discoveredById.Count == 0)
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, localFailure);
        }

        var matchedManifests = discoveredById.Values
            .Where(manifest => loadedRecords.TryGetValue(manifest.Id, out var records) &&
                records.Length == 1 &&
                string.Equals(records[0].Version, manifest.Version.ToString(), StringComparison.Ordinal))
            .ToImmutableArray();
        var graph = ExtensionManifestGraph.ValidateAndOrder(
            matchedManifests,
            new SemVersion(
                HostApiVersion.Current.Major,
                HostApiVersion.Current.Minor,
                HostApiVersion.Current.Patch));
        if (!graph.Succeeded)
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, true);
        }

        var requestedHandlerIds = snapshot.Routes
            .Select(static route => route?.Target)
            .OfType<ExtensionHandlerRouteTargetConfiguration>()
            .Select(static target => target.HandlerId)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        var settingsById = snapshot.ExtensionSettings
            .Where(static setting => setting is not null)
            .ToDictionary(static setting => setting.ExtensionId, StringComparer.Ordinal);
        var desired = ImmutableArray.CreateBuilder<ExtensionRuntimeDescriptor>();

        foreach (var manifest in graph.OrderedManifests)
        {
            if (!loadedRecords.TryGetValue(manifest.Id, out var records) ||
                records.Length != 1 ||
                !string.Equals(records[0].Version, manifest.Version.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            settingsById.TryGetValue(manifest.Id, out var settings);
            desired.Add(new ExtensionRuntimeDescriptor(
                manifest,
                settings,
                requestedHandlerIds,
                includeFallback: true));

        }

        return new(desired.ToImmutable(), localFailure);
    }

    private static bool HasUnsafeUnavailableBinding(ExtensionDispatchGeneration generation) =>
        generation.Bindings.Any(static binding =>
            !binding.Available &&
            binding.FailureCode is not ExtensionFailureCode.None and
                not ExtensionFailureCode.HandlerUnavailable and
                not ExtensionFailureCode.FallbackConflict);

    private static bool CanReusePriorLoadedIdentities(
        HostRoutingSnapshot previousSnapshot,
        HostConfigurationSnapshot nextSnapshot)
    {
        var previous = BuildLoadedIdentities(previousSnapshot.Configuration);
        var next = BuildLoadedIdentities(nextSnapshot);
        return previous is not null &&
            next is not null &&
            previous.All(pair => next.TryGetValue(pair.Key, out var identity) &&
                identity.Equals(pair.Value));
    }

    private static Dictionary<string, LoadedExtensionIdentity>? BuildLoadedIdentities(
        HostConfigurationSnapshot snapshot)
    {
        var records = new Dictionary<string, ExtensionRecordConfiguration>(StringComparer.Ordinal);
        foreach (var record in snapshot.ExtensionRecords)
        {
            if (record is null ||
                record.LoadState != ExtensionLoadState.Loaded ||
                !records.TryAdd(record.ExtensionId, record))
            {
                if (record is null || record.LoadState != ExtensionLoadState.Loaded)
                {
                    continue;
                }

                return null;
            }
        }

        var settings = new Dictionary<string, ExtensionSettingsConfiguration>(StringComparer.Ordinal);
        foreach (var setting in snapshot.ExtensionSettings)
        {
            if (setting is null || !records.ContainsKey(setting.ExtensionId))
            {
                continue;
            }

            if (!settings.TryAdd(setting.ExtensionId, setting))
            {
                return null;
            }
        }

        return records.ToDictionary(
            static pair => pair.Key,
            pair => new LoadedExtensionIdentity(
                pair.Value.Version,
                pair.Value.RecordVersion,
                settings.TryGetValue(pair.Key, out var setting)
                    ? new ExtensionSettingsIdentity(
                        setting.SchemaVersion,
                        setting.SettingsJson,
                        setting.Version)
                    : null),
            StringComparer.Ordinal);
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
