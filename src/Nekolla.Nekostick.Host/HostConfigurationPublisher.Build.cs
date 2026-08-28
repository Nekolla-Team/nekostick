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
        ImmutableHashSet<string> ForceReloadIds);
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

    private const int MaxBootstrapReloadAttempts = 3;

    /// <summary>Builds the desired extension generation from durable records and discovered manifests.</summary>
    /// <param name="snapshot">The durable Host configuration snapshot.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <param name="reloadAttempt">The bounded bootstrap persistence retry number.</param>
    /// <param name="forceReloadIds">The extension identifiers that must be re-candidated even when their descriptors are unchanged.</param>
    /// <returns>The desired descriptors and publication metadata.</returns>
    private async ValueTask<DesiredExtensionSet> BuildDesiredAsync(
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken,
        int reloadAttempt = 0,
        ImmutableHashSet<string>? forceReloadIds = null)
    {
        var requestedForceReloadIds = forceReloadIds ?? EmptyForceReloadIds;
        if (_nodeOptions.SkipExtensions)
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, false, requestedForceReloadIds);
        }

        var durableRecords = new Dictionary<string, ExtensionRecordConfiguration>(StringComparer.Ordinal);
        var invalidDurableRecords = false;
        foreach (var record in snapshot.ExtensionRecords)
        {
            if (record is null || !durableRecords.TryAdd(record.ExtensionId, record))
            {
                invalidDurableRecords = true;
            }
        }

        var loadedRecords = durableRecords
            .Where(static pair => pair.Value.LoadState == ExtensionLoadState.Loaded)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var bootstrapState = durableRecords.Count == 0
            ? ExtensionLoadState.Loaded
            : ExtensionLoadState.Disabled;
        var canPersistBootstrapRecords = !_nodeOptions.ReadOnly &&
            (_runtimeState is null || _runtimeState.ExtensionConfigurationWritesAllowed);
        var installRoot = Path.Combine(AppContext.BaseDirectory, "extensions");
        if (!Directory.Exists(installRoot))
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, invalidDurableRecords || loadedRecords.Count != 0, requestedForceReloadIds);
        }

        var discoveredById = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
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
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, true, requestedForceReloadIds);
        }

        foreach (var directory in directories)
        {
            ManifestDiscoveryResult result;
            try { result = ExtensionManifestDiscovery.Discover(directory); }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, "BuildDesired.ManifestDiscovery");
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

        if (invalidDurableRecords || duplicateIds.Count != 0)
        {
            throw new InvalidOperationException("The durable extension records or discovered identifiers are invalid.");
        }

        var localFailure = loadedRecords.Any(pair =>
            !discoveredById.TryGetValue(pair.Key, out var manifest) ||
            !string.Equals(pair.Value.Version, manifest.Version.ToString(), StringComparison.Ordinal));
        if (discoveredById.Count == 0)
        {
            return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, localFailure, requestedForceReloadIds);
        }

        // Existing records are authoritative. A missing record is predicted Loaded only for
        // the zero-record first-run bootstrap when durable bootstrap writes are permitted.
        // Validate only load-relevant manifests so an invalid Disabled or newly discovered-to-be-Disabled manifest cannot poison publication.
        var loadableManifests = discoveredById.Values
            .Where(manifest => durableRecords.TryGetValue(manifest.Id, out var record)
                ? record.LoadState == ExtensionLoadState.Loaded &&
                  string.Equals(record.Version, manifest.Version.ToString(), StringComparison.Ordinal)
                : bootstrapState == ExtensionLoadState.Loaded && canPersistBootstrapRecords)
            .ToImmutableArray();
        var graph = ExtensionManifestGraph.ValidateAndOrder(
            loadableManifests,
            new SemVersion(HostApiVersion.Current.Major, HostApiVersion.Current.Minor, HostApiVersion.Current.Patch));
        if (!graph.Succeeded)
        {
            throw new InvalidOperationException("The loadable extension graph is invalid.");
        }

        var absentManifests = discoveredById.Values
            .Where(manifest => !durableRecords.ContainsKey(manifest.Id))
            .ToImmutableArray();
        var persistedIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        if (absentManifests.Length != 0 && canPersistBootstrapRecords)
        {
            if (_dbContextFactory is null)
            {
                // A discovered extension must never start without its durable record.
                throw new InvalidOperationException("Extension records require a durable configuration store.");
            }

            var persistence = await PersistBootstrapRecordsAsync(
                    bootstrapState,
                    snapshot,
                    absentManifests,
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

            persistedIds = bootstrapState == ExtensionLoadState.Loaded
                ? absentManifests
                    .Select(static manifest => manifest.Id)
                    .ToImmutableHashSet(StringComparer.Ordinal)
                : ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        }

        var routesByOwner = snapshot.Routes
            .Where(static route => route is not null)
            .Select(route => new { Route = route, Owner = _routeOwners.TryGetValue(route.Id, out var owner) ? owner : null })
            .Where(static value => value.Owner is not null)
            .GroupBy(static value => value.Owner!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Select(value => value.Route.Id).ToImmutableArray(), StringComparer.Ordinal);
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

        return new(desired.ToImmutable(), localFailure, requestedForceReloadIds);
    }

    private async ValueTask<ConfigurationWriteResult> PersistBootstrapRecordsAsync(
        ExtensionLoadState initialState,
        HostConfigurationSnapshot snapshot,
        IEnumerable<ExtensionManifest> manifests,
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
                recordVersion: 0))
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
