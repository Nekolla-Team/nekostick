using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostConfigurationPublisher
{
    private readonly record struct DesiredExtensionSet(
        ImmutableArray<ExtensionRuntimeDescriptor> Descriptors,
        bool HasUnavailableLoadedRecord,
        bool HasQuarantinedLoadedRecord,
        ImmutableHashSet<string> ForceReloadIds,
        ImmutableArray<ExtensionNodeStateWrite> NodeStates);
    private static readonly ImmutableHashSet<string> EmptyForceReloadIds =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    private readonly record struct ExtensionSettingsIdentity(
        int SchemaVersion,
        string SettingsJson,
        long Version);

    private readonly record struct LoadedExtensionIdentity(
        string Version,
        long RecordVersion,
        ExtensionSettingsIdentity? Settings);

    private readonly record struct GraphSelection(
        ImmutableArray<ExtensionManifest> OrderedManifests,
        ImmutableDictionary<string, string> QuarantinedIds);

    private const int MaxBootstrapReloadAttempts = 3;

    /// <summary>Builds the desired extension generation from durable records and discovered manifests.</summary>
    /// <param name="snapshot">The durable Host configuration snapshot.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <param name="reloadAttempt">The bounded bootstrap persistence retry number.</param>
    /// <param name="forceReloadIds">The extension identifiers that must be re-candidated even when their descriptors are unchanged.</param>
    /// <returns>The desired descriptors, node-local observations, and publication metadata.</returns>
    private async ValueTask<DesiredExtensionSet> BuildDesiredAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken,
        int reloadAttempt = 0,
        ImmutableHashSet<string>? forceReloadIds = null)
    {
        var requestedForceReloadIds = forceReloadIds ?? EmptyForceReloadIds;
        if (_nodeOptions.SkipExtensions)
        {
            return new(
                ImmutableArray<ExtensionRuntimeDescriptor>.Empty,
                false,
                false,
                requestedForceReloadIds,
                ImmutableArray<ExtensionNodeStateWrite>.Empty);
        }

        var durableRecords = new Dictionary<string, ExtensionRecordConfiguration>(StringComparer.Ordinal);
        var duplicateDurableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in snapshot.ExtensionRecords)
        {
            if (record is null || !IsDurableRecordSafe(record))
            {
                continue;
            }

            if (!durableRecords.TryAdd(record.ExtensionId, record))
            {
                duplicateDurableIds.Add(record.ExtensionId);
            }
        }

        var observations = new Dictionary<string, ExtensionNodeStateWrite>(StringComparer.Ordinal);
        foreach (var pair in durableRecords)
        {
            observations[pair.Key] = new(
                pair.Key,
                null,
                pair.Value.LoadState,
                "None");
        }

        var loadedRecords = durableRecords
            .Where(static pair => pair.Value.LoadState == ExtensionLoadState.Loaded)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var bootstrapState = durableRecords.Count == 0
            ? ExtensionLoadState.Loaded
            : ExtensionLoadState.Disabled;
        var canPersistBootstrapRecords = !_nodeOptions.ReadOnly &&
            (_runtimeState is null || _runtimeState.ExtensionConfigurationWritesAllowed);
        var installRoot = _nodeOptions.ExtensionsRootPath;
        if (!Directory.Exists(installRoot))
        {
            foreach (var pair in loadedRecords)
            {
                observations[pair.Key] = Observation(
                    pair.Key,
                    null,
                    ExtensionLoadState.Failed,
                    "ManifestMissing");
            }

            return CreateDesiredSet(
                ImmutableArray<ExtensionRuntimeDescriptor>.Empty,
                observations,
                loadedRecords.Keys.ToImmutableHashSet(StringComparer.Ordinal),
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                requestedForceReloadIds);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(installRoot)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "BuildDesired.EnumerateExtensions");
            foreach (var pair in loadedRecords)
            {
                observations[pair.Key] = Observation(
                    pair.Key,
                    null,
                    ExtensionLoadState.Failed,
                    "ManifestDiscovery");
            }

            return CreateDesiredSet(
                ImmutableArray<ExtensionRuntimeDescriptor>.Empty,
                observations,
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                loadedRecords.Keys.ToImmutableHashSet(StringComparer.Ordinal),
                requestedForceReloadIds);
        }

        var discoveredById = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        var contentHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            ManifestDiscoveryResult result;
            try
            {
                result = ExtensionManifestDiscovery.Discover(directory);
            }
            catch (Exception exception)
            {
                HostLogMessages.ExtensionDirectorySkipped(
                    _logger,
                    DirectoryName(directory),
                    ExtensionFailureCode.LoadFailed.ToString());
                HostLogMessages.FailureDetails(_logger, exception, "BuildDesired.ManifestDiscovery");
                continue;
            }

            if (!result.Succeeded || result.Manifest is null)
            {
                HostLogMessages.ExtensionDirectorySkipped(
                    _logger,
                    DirectoryName(directory),
                    result.FailureCode.ToString());
                continue;
            }

            var manifest = result.Manifest;
            if (duplicateIds.Contains(manifest.Id))
            {
                HostLogMessages.DuplicateExtensionManifestId(_logger, manifest.Id);
                continue;
            }

            if (!discoveredById.TryAdd(manifest.Id, manifest))
            {
                discoveredById.Remove(manifest.Id);
                contentHashes.Remove(manifest.Id);
                duplicateIds.Add(manifest.Id);
                HostLogMessages.DuplicateExtensionManifestId(_logger, manifest.Id);
                continue;
            }

            contentHashes[manifest.Id] = ExtensionContentDigest.TryCompute(manifest);
        }

        var quarantinedIds = new HashSet<string>(StringComparer.Ordinal);
        var unavailableLoadedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in duplicateDurableIds)
        {
            quarantinedIds.Add(id);
            if (loadedRecords.ContainsKey(id))
            {
                observations[id] = Observation(id, null, ExtensionLoadState.Failed, "DuplicateDurableRecord");
            }
        }

        foreach (var id in duplicateIds)
        {
            quarantinedIds.Add(id);
            if (loadedRecords.ContainsKey(id))
            {
                observations[id] = Observation(id, null, ExtensionLoadState.Failed, "DuplicateManifestId");
            }
        }

        var loadableManifests = ImmutableArray.CreateBuilder<ExtensionManifest>();
        foreach (var pair in loadedRecords)
        {
            if (quarantinedIds.Contains(pair.Key))
            {
                continue;
            }

            if (!discoveredById.TryGetValue(pair.Key, out var manifest))
            {
                observations[pair.Key] = Observation(pair.Key, null, ExtensionLoadState.Failed, "ManifestMissing");
                unavailableLoadedIds.Add(pair.Key);
                continue;
            }

            var observedHash = contentHashes[pair.Key];
            if (!string.Equals(pair.Value.Version, manifest.Version.ToString(), StringComparison.Ordinal))
            {
                observations[pair.Key] = Observation(pair.Key, observedHash, ExtensionLoadState.Failed, "VersionMismatch");
                unavailableLoadedIds.Add(pair.Key);
                continue;
            }

            if (pair.Value.ContentHash is not null)
            {
                if (observedHash is null)
                {
                    // A durable pin exists but the local digest is transiently uncomputable:
                    // fail closed with a distinct code so diagnostics can tell it from real drift.
                    observations[pair.Key] = Observation(
                        pair.Key,
                        null,
                        ExtensionLoadState.Failed,
                        "ContentHashMissing");
                    quarantinedIds.Add(pair.Key);
                    continue;
                }

                if (!string.Equals(pair.Value.ContentHash, observedHash, StringComparison.OrdinalIgnoreCase))
                {
                    observations[pair.Key] = Observation(
                        pair.Key,
                        observedHash,
                        ExtensionLoadState.Failed,
                        "ContentMismatch");
                    quarantinedIds.Add(pair.Key);
                    continue;
                }
            }

            loadableManifests.Add(manifest);
            observations[pair.Key] = Observation(
                pair.Key,
                observedHash,
                ExtensionLoadState.Loaded,
                pair.Value.ContentHash is null ? "ContentHashMissing" : "None");
        }

        var absentManifests = discoveredById.Values
            .Where(manifest => !durableRecords.ContainsKey(manifest.Id))
            .ToImmutableArray();
        if (bootstrapState == ExtensionLoadState.Loaded && canPersistBootstrapRecords)
        {
            loadableManifests.AddRange(absentManifests);
        }

        var graphSelection = BuildLoadableGraph(loadableManifests.ToImmutable());
        foreach (var pair in graphSelection.QuarantinedIds)
        {
            quarantinedIds.Add(pair.Key);
            if (loadedRecords.ContainsKey(pair.Key))
            {
                observations[pair.Key] = Observation(
                    pair.Key,
                    contentHashes.TryGetValue(pair.Key, out var hash) ? hash : null,
                    ExtensionLoadState.Failed,
                    pair.Value);
            }
        }

        var persistedIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        var manifestsToPersist = bootstrapState == ExtensionLoadState.Loaded
            ? graphSelection.OrderedManifests.Where(manifest => !durableRecords.ContainsKey(manifest.Id)).ToImmutableArray()
            : absentManifests;
        if (manifestsToPersist.Length != 0 && canPersistBootstrapRecords)
        {
            if (_dbContextFactory is null)
            {
                foreach (var manifest in manifestsToPersist)
                {
                    quarantinedIds.Add(manifest.Id);
                }
            }
            else
            {
                var persistence = await PersistBootstrapRecordsAsync(
                        bootstrapState,
                        snapshot,
                        manifestsToPersist,
                        contentHashes,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!persistence.IsSuccess)
                {
                    var isConflict = persistence.Errors.Any(static error =>
                        error.Code == ConfigurationErrorCode.ConcurrencyConflict);
                    if (isConflict && reloadAttempt < MaxBootstrapReloadAttempts)
                    {
                        var latest = await ReloadBootstrapSnapshotAsync(snapshot, cancellationToken)
                            .ConfigureAwait(false);
                        if (latest is not null && latest.Version > snapshot.Version)
                        {
                            return await BuildDesiredAsync(
                                    latest,
                                    cancellationToken,
                                    reloadAttempt + 1,
                                    requestedForceReloadIds)
                                .ConfigureAwait(false);
                        }
                    }

                    throw new InvalidOperationException("Extension records could not be durably persisted.");
                }

                persistedIds = manifestsToPersist
                    .Select(static manifest => manifest.Id)
                    .ToImmutableHashSet(StringComparer.Ordinal);
                foreach (var manifest in manifestsToPersist)
                {
                    var hasHash = contentHashes.TryGetValue(manifest.Id, out var hash);
                    observations[manifest.Id] = Observation(
                        manifest.Id,
                        hasHash ? hash : null,
                        bootstrapState,
                        hasHash && hash is not null ? "None" : "ContentHashMissing");
                }
            }
        }

        var routesByOwner = snapshot.Routes
            .Where(static route => route is not null)
            .Select(route => new
            {
                Route = route,
                Owner = _routeOwners.TryGetValue(route.Id, out var owner) ? owner : null
            })
            .Where(static value => value.Owner is not null)
            .GroupBy(static value => value.Owner!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(value => value.Route.Id).ToImmutableArray(),
                StringComparer.Ordinal);
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
        foreach (var manifest in graphSelection.OrderedManifests)
        {
            var isNewlyPersisted = persistedIds.Contains(manifest.Id);
            if (!isNewlyPersisted &&
                (!loadedRecords.TryGetValue(manifest.Id, out var record) ||
                 !string.Equals(record.Version, manifest.Version.ToString(), StringComparison.Ordinal)))
            {
                continue;
            }

            settingsById.TryGetValue(manifest.Id, out var settings);
            routesByOwner.TryGetValue(manifest.Id, out var ownedRouteIds);
            ownedRouteIds = ownedRouteIds.IsDefault ? ImmutableArray<Guid>.Empty : ownedRouteIds;
            desired.Add(new ExtensionRuntimeDescriptor(manifest, settings, requestedHandlerIds, true, ownedRouteIds));
        }

        return CreateDesiredSet(
            desired.ToImmutable(),
            observations,
            unavailableLoadedIds,
            quarantinedIds,
            requestedForceReloadIds);
    }

    private static DesiredExtensionSet CreateDesiredSet(
        ImmutableArray<ExtensionRuntimeDescriptor> descriptors,
        Dictionary<string, ExtensionNodeStateWrite> observations,
        IEnumerable<string> unavailableLoadedIds,
        IEnumerable<string> quarantinedIds,
        ImmutableHashSet<string> forceReloadIds) =>
        new(
            descriptors,
            unavailableLoadedIds.Any(),
            quarantinedIds.Any(quarantinedId =>
                observations.TryGetValue(quarantinedId, out var state) &&
                state.LoadState == ExtensionLoadState.Failed),
            forceReloadIds,
            observations.Values
                .OrderBy(static state => state.ExtensionId, StringComparer.Ordinal)
                .ToImmutableArray());
    private static string DirectoryName(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static bool IsDurableRecordSafe(ExtensionRecordConfiguration record) =>
        !string.IsNullOrWhiteSpace(record.ExtensionId) &&
        record.ExtensionId.Length <= 128 &&
        !record.ExtensionId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(record.Version) &&
        record.RecordVersion >= 0 &&
        Enum.IsDefined(record.LoadState);


    private static ExtensionNodeStateWrite Observation(
        string extensionId,
        string? observedHash,
        ExtensionLoadState state,
        string failureCode) =>
        new(extensionId, observedHash, state, failureCode);

    private GraphSelection BuildLoadableGraph(ImmutableArray<ExtensionManifest> candidates)
    {
        var remaining = candidates.ToDictionary(static manifest => manifest.Id, StringComparer.Ordinal);
        var quarantined = new Dictionary<string, string>(StringComparer.Ordinal);
        while (remaining.Count != 0)
        {
            var graph = ExtensionManifestGraph.ValidateAndOrder(
                remaining.Values,
                new SemVersion(_hostApiVersion.Major, _hostApiVersion.Minor, _hostApiVersion.Patch));
            if (graph.Succeeded)
            {
                return new(graph.OrderedManifests, quarantined.ToImmutableDictionary(StringComparer.Ordinal));
            }

            var affected = FindGraphAffectedIds(remaining.Values, graph.FailureCode);
            if (affected.Count == 0)
            {
                affected = remaining.Keys.ToHashSet(StringComparer.Ordinal);
            }

            foreach (var id in affected)
            {
                if (remaining.Remove(id))
                {
                    quarantined[id] = graph.FailureCode.ToString();
                }
            }
        }

        return new(ImmutableArray<ExtensionManifest>.Empty, quarantined.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private HashSet<string> FindGraphAffectedIds(
        IEnumerable<ExtensionManifest> manifests,
        ExtensionFailureCode failureCode)
    {
        var items = manifests.ToDictionary(static manifest => manifest.Id, StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        switch (failureCode)
        {
            case ExtensionFailureCode.HostApiIncompatible:
            {
                var hostApi = new SemVersion(_hostApiVersion.Major, _hostApiVersion.Minor, _hostApiVersion.Patch);
                affected.UnionWith(items.Values
                    .Where(manifest => !manifest.RequiredHostApiVersion.IsSatisfiedBy(hostApi))
                    .Select(static manifest => manifest.Id));
                break;
            }
            case ExtensionFailureCode.MissingDependency:
                foreach (var manifest in items.Values)
                {
                    if (manifest.Dependencies.Any(dependency => !items.ContainsKey(dependency.Id)))
                    {
                        affected.Add(manifest.Id);
                    }
                }

                break;
            case ExtensionFailureCode.DependencyVersionIncompatible:
                foreach (var manifest in items.Values)
                {
                    if (manifest.Dependencies.Any(dependency =>
                        items.TryGetValue(dependency.Id, out var target) &&
                        !dependency.VersionRange.IsSatisfiedBy(target.Version)))
                    {
                        affected.Add(manifest.Id);
                    }
                }

                break;
            case ExtensionFailureCode.DependencyCycle:
                affected = FindCycleIds(items);
                break;
            case ExtensionFailureCode.DuplicateContractDeclaration:
                foreach (var manifest in items.Values)
                {
                    if (manifest.Exports.GroupBy(static export => export.ContractId, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
                        manifest.Imports.GroupBy(static import => import.ContractId, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    {
                        affected.Add(manifest.Id);
                    }
                }

                foreach (var group in items.Values
                             .SelectMany(manifest => manifest.Exports.Select(export => new { manifest.Id, export.ContractId }))
                             .GroupBy(static value => value.ContractId, StringComparer.Ordinal))
                {
                    if (group.Count() > 1)
                    {
                        affected.UnionWith(group.Select(static value => value.Id));
                    }
                }

                break;
            case ExtensionFailureCode.MissingContractProvider:
                var providers = items.Values
                    .SelectMany(manifest => manifest.Exports.Select(export => new { export.ContractId, manifest.Id }))
                    .GroupBy(static value => value.ContractId, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.Ordinal);
                foreach (var manifest in items.Values)
                {
                    if (manifest.Imports.Any(import => !providers.ContainsKey(import.ContractId)))
                    {
                        affected.Add(manifest.Id);
                    }
                }

                break;
            case ExtensionFailureCode.ContractVersionIncompatible:
            case ExtensionFailureCode.ContractIdentityMismatch:
                var contracts = items.Values
                    .SelectMany(manifest => manifest.Exports.Select(export => new { export.ContractId, Export = export, manifest.Id }))
                    .GroupBy(static value => value.ContractId, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
                foreach (var manifest in items.Values)
                {
                    foreach (var import in manifest.Imports)
                    {
                        if (!contracts.TryGetValue(import.ContractId, out var provider))
                        {
                            continue;
                        }

                        var incompatible = failureCode == ExtensionFailureCode.ContractVersionIncompatible
                            ? !import.VersionRange.IsSatisfiedBy(provider.Export.Version)
                            : !string.Equals(import.AssemblyIdentity, provider.Export.AssemblyIdentity, StringComparison.Ordinal) ||
                              !string.Equals(import.TypeIdentity, provider.Export.TypeIdentity, StringComparison.Ordinal);
                        if (incompatible)
                        {
                            affected.Add(manifest.Id);
                        }
                    }
                }

                break;
            case ExtensionFailureCode.ContractCatalogUnavailable:
                affected.UnionWith(items.Keys);
                break;
            default:
                affected.UnionWith(items.Keys);
                break;
        }


        return affected;
    }

    private static HashSet<string> FindCycleIds(Dictionary<string, ExtensionManifest> manifests)
    {
        var providers = manifests.Values
            .SelectMany(manifest => manifest.Exports.Select(export => new { export.ContractId, manifest.Id }))
            .GroupBy(static value => value.ContractId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.Ordinal);
        var edges = manifests.Keys.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var manifest in manifests.Values)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (manifests.ContainsKey(dependency.Id))
                {
                    edges[manifest.Id].Add(dependency.Id);
                }
            }

            foreach (var import in manifest.Imports)
            {
                if (providers.TryGetValue(import.ContractId, out var provider) &&
                    !string.Equals(provider, manifest.Id, StringComparison.Ordinal))
                {
                    edges[manifest.Id].Add(provider);
                }
            }
        }

        var indegrees = manifests.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            foreach (var target in edge.Value)
            {
                indegrees[target]++;
            }
        }

        var ready = new Queue<string>(indegrees.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
        while (ready.Count != 0)
        {
            var id = ready.Dequeue();
            foreach (var target in edges[id])
            {
                if (--indegrees[target] == 0)
                {
                    ready.Enqueue(target);
                }
            }
        }

        return indegrees
            .Where(static pair => pair.Value > 0)
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async ValueTask<ConfigurationWriteResult> PersistBootstrapRecordsAsync(
        ExtensionLoadState initialState,
        HostConfigurationSnapshot snapshot,
        IEnumerable<ExtensionManifest> manifests,
        Dictionary<string, string?> contentHashes,
        CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
        }

        var now = DateTimeOffset.UtcNow;
        var records = manifests
            .Select(manifest => new ExtensionRecordConfiguration(
                manifest.Id,
                manifest.Version.ToString(),
                initialState,
                now,
                now,
                recordVersion: 0,
                contentHash: contentHashes.TryGetValue(manifest.Id, out var hash) ? hash : null))
            .ToImmutableArray();
        try
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var api = new EfHostConfigApi(db);
            return await api
                .PersistDiscoveredExtensionRecordsAsync(
                    initialState,
                    snapshot.Version,
                    records,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "BuildDesired.BootstrapRecordPersistence");
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
        }
    }

    private async ValueTask<HostConfigurationSnapshot?> ReloadBootstrapSnapshotAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.Version > snapshot.Version)
        {
            return latest;
        }

        if (_dbContextFactory is null)
        {
            return null;
        }

        try
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var api = new EfHostConfigApi(db);
            var result = await api.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return result.IsSuccess && result.Value is { Version: > 0 } durable &&
                durable.Version > snapshot.Version
                ? durable
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "BuildDesired.BootstrapReload");
            return null;
        }
    }

    private static bool HasUnsafeUnavailableBinding(ExtensionDispatchGeneration generation) =>
        generation.Bindings.Any(static binding => !binding.Available && binding.FailureCode is not ExtensionFailureCode.None and not ExtensionFailureCode.HandlerUnavailable and not ExtensionFailureCode.FallbackConflict);

    private static bool CanReusePriorLoadedIdentities(HostRoutingSnapshot previousSnapshot, HostConfigurationSnapshot nextSnapshot)
    {
        var previous = BuildLoadedIdentities(previousSnapshot.Configuration);
        var next = BuildLoadedIdentities(nextSnapshot);
        return previous is not null && next is not null && previous.All(pair => next.TryGetValue(pair.Key, out var identity) && identity.Equals(pair.Value));
    }

    private static Dictionary<string, LoadedExtensionIdentity>? BuildLoadedIdentities(HostConfigurationSnapshot snapshot)
    {
        var records = new Dictionary<string, ExtensionRecordConfiguration>(StringComparer.Ordinal);
        foreach (var record in snapshot.ExtensionRecords)
        {
            if (record is null || record.LoadState != ExtensionLoadState.Loaded) continue;
            if (!records.TryAdd(record.ExtensionId, record)) return null;
        }
        var settings = new Dictionary<string, ExtensionSettingsConfiguration>(StringComparer.Ordinal);
        foreach (var setting in snapshot.ExtensionSettings)
        {
            if (setting is null || !records.ContainsKey(setting.ExtensionId)) continue;
            if (!settings.TryAdd(setting.ExtensionId, setting)) return null;
        }
        return records.ToDictionary(static pair => pair.Key, pair => new LoadedExtensionIdentity(
            pair.Value.Version, pair.Value.RecordVersion,
            settings.TryGetValue(pair.Key, out var setting) ? new ExtensionSettingsIdentity(setting.SchemaVersion, setting.SettingsJson, setting.Version) : null), StringComparer.Ordinal);
    }
}
