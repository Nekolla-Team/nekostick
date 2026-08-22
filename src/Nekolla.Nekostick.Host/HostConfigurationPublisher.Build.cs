using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostConfigurationPublisher
{
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
        var bootstrap = snapshot.ExtensionRecords.Length == 0;
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
            directories = Directory.EnumerateDirectories(installRoot)
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
            try { result = ExtensionManifestDiscovery.Discover(directory); }
            catch { continue; }
            if (!result.Succeeded || result.Manifest is null) continue;
            var manifest = result.Manifest;
            if (duplicateIds.Contains(manifest.Id)) continue;
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

        var matchedManifests = bootstrap
            ? discoveredById.Values.ToImmutableArray()
            : discoveredById.Values.Where(manifest => loadedRecords.TryGetValue(manifest.Id, out var records) &&
                records.Length == 1 && string.Equals(records[0].Version, manifest.Version.ToString(), StringComparison.Ordinal)).ToImmutableArray();
        var graph = ExtensionManifestGraph.ValidateAndOrder(
            matchedManifests,
            new SemVersion(HostApiVersion.Current.Major, HostApiVersion.Current.Minor, HostApiVersion.Current.Patch));
        if (!graph.Succeeded) return new(ImmutableArray<ExtensionRuntimeDescriptor>.Empty, true);

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
            if (!bootstrap && (!loadedRecords.TryGetValue(manifest.Id, out var records) || records.Length != 1 ||
                !string.Equals(records[0].Version, manifest.Version.ToString(), StringComparison.Ordinal))) continue;
            settingsById.TryGetValue(manifest.Id, out var settings);
            routesByOwner.TryGetValue(manifest.Id, out var ownedRouteIds);
            ownedRouteIds = ownedRouteIds.IsDefault ? ImmutableArray<Guid>.Empty : ownedRouteIds;
            desired.Add(new ExtensionRuntimeDescriptor(manifest, settings, requestedHandlerIds, true, ownedRouteIds));
        }
        return new(desired.ToImmutable(), localFailure);
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
