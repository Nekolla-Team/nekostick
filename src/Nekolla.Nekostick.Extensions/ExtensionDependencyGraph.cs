using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Validates dependencies and produces a deterministic topological order.</summary>
public static class ExtensionManifestGraph
{
    /// <summary>Validates an explicitly supplied manifest set.</summary>
    /// <param name="manifests">The manifests already discovered by the caller.</param>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    /// <param name="contractCatalog">The immutable host-owned shared contract catalog.</param>
    /// <param name="logger">The optional host logger for validation diagnostics.</param>
    /// <returns>A graph result whose layers are ordinal ID sorted.</returns>
    public static ExtensionGraphResult ValidateAndOrder(
        IEnumerable<ExtensionManifest>? manifests,
        SemVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null,
        ILogger? logger = null)
    {
        if (manifests is null)
        {
            return ExtensionGraphResult.Failure(
                ExtensionFailureCode.InvalidArgument,
                new ExtensionErrorDetail("An extension manifest collection is required."));
        }

        ImmutableArray<ExtensionManifest> items;
        try
        {
            items = manifests.ToImmutableArray();
        }
        catch (Exception exception)
        {
            if (logger is { } diagnosticLogger)
            {
                ExtensionLogMessages.ExtensionDependencyGraphFailed(
                    diagnosticLogger,
                    exception,
                    nameof(ValidateAndOrder));
            }

            return ExtensionGraphResult.Failure(
                ExtensionFailureCode.InvalidArgument,
                ExtensionErrorDetail.FromException(exception));
        }

        var byId = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        foreach (var manifest in items)
        {
            if (manifest is null || !ExtensionIdentifierSyntax.IsValid(manifest.Id))
            {
                return ExtensionGraphResult.Failure(
                    ExtensionFailureCode.InvalidIdentifier,
                    new ExtensionErrorDetail("A discovered manifest is missing or has an invalid extension identifier."));
            }

            if (!byId.TryAdd(manifest.Id, manifest))
            {
                return ExtensionGraphResult.Failure(
                    ExtensionFailureCode.DuplicateExtensionId,
                    new ExtensionErrorDetail($"More than one manifest declares extension identifier '{manifest.Id}'."));
            }

            if (!manifest.RequiredHostApiVersion.IsSatisfiedBy(hostApiVersion))
            {
                return ExtensionGraphResult.Failure(
                    ExtensionFailureCode.HostApiIncompatible,
                    new ExtensionErrorDetail(
                        $"Extension '{manifest.Id}' requires Host API version range '{manifest.RequiredHostApiVersion}', which is not satisfied by Host API version '{hostApiVersion}'."));
            }
        }
        var contractProviders = new Dictionary<string, string>(StringComparer.Ordinal);
        var contractExports = new Dictionary<string, ExtensionContractExport>(StringComparer.Ordinal);
        var contractFailure = ValidateContracts(items, contractCatalog, contractProviders, contractExports);
        if (contractFailure is not null)
        {
            return contractFailure;
        }

        var edges = new List<(string From, string To, bool Optional)>();
        foreach (var manifest in items)
        {
            var edgeTargets = new Dictionary<string, bool>(StringComparer.Ordinal);

            var dependencies = manifest.Dependencies;
            var uniqueDependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in dependencies)
            {
                if (!uniqueDependencies.Add(dependency.Id))
                {
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.DuplicateExtensionId,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' declares dependency '{dependency.Id}' more than once."));
                }

                if (!byId.TryGetValue(dependency.Id, out var dependencyManifest))
                {
                    if (dependency.Optional)
                    {
                        continue;
                    }

                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.MissingDependency,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' requires missing extension '{dependency.Id}'."));
                }

                if (!dependency.VersionRange.IsSatisfiedBy(dependencyManifest.Version))
                {
                    if (dependency.Optional)
                    {
                        continue;
                    }

                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.DependencyVersionIncompatible,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' requires dependency '{dependency.Id}' in version range '{dependency.VersionRange}', but available version '{dependencyManifest.Version}' does not match."));
                }

                AddEdge(edgeTargets, dependency.Id, dependency.Optional);
            }

            foreach (var import in manifest.Imports)
            {
                // Edges exist only for satisfied imports; an unsatisfied optional import contributes nothing.
                if (!contractProviders.TryGetValue(import.ContractId, out var providerId) ||
                    string.Equals(providerId, manifest.Id, StringComparison.Ordinal) ||
                    !import.VersionRange.IsSatisfiedBy(contractExports[import.ContractId].Version))
                {
                    continue;
                }

                AddEdge(edgeTargets, providerId, import.Optional);
            }

            foreach (var edge in edgeTargets)
            {
                edges.Add((edge.Key, manifest.Id, edge.Value));
            }
        }

        var ordered = Order(items, byId, edges, includeOptionalEdges: true);
        if (ordered is null && edges.Any(static edge => edge.Optional))
        {
            // Optional relationships never block loading: drop them to break cycles they participate in.
            ordered = Order(items, byId, edges, includeOptionalEdges: false);
        }

        return ordered is { } result
            ? ExtensionGraphResult.Success(result)
            : ExtensionGraphResult.Failure(
                ExtensionFailureCode.DependencyCycle,
                new ExtensionErrorDetail(
                    "The extension dependency graph contains a cycle that cannot be resolved by dropping optional edges."));
    }

    private static void AddEdge(Dictionary<string, bool> edgeTargets, string target, bool optional)
    {
        // A relationship declared both required (dependency/import) and optional stays required.
        edgeTargets[target] = edgeTargets.TryGetValue(target, out var existing) ? existing && optional : optional;
    }

    private static ImmutableArray<ExtensionManifest>? Order(
        ImmutableArray<ExtensionManifest> items,
        Dictionary<string, ExtensionManifest> byId,
        List<(string From, string To, bool Optional)> edges,
        bool includeOptionalEdges)
    {
        var indegrees = items.ToDictionary(static manifest => manifest.Id, static _ => 0, StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (from, to, optional) in edges)
        {
            if (optional && !includeOptionalEdges)
            {
                continue;
            }

            indegrees[to]++;
            if (!dependents.TryGetValue(from, out var dependentList))
            {
                dependentList = new List<string>();
                dependents.Add(from, dependentList);
            }

            dependentList.Add(to);
        }

        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in indegrees)
        {
            if (item.Value == 0)
            {
                ready.Add(item.Key);
            }
        }

        var ordered = ImmutableArray.CreateBuilder<ExtensionManifest>(items.Length);
        while (ready.Count > 0)
        {
            var layer = ready.ToArray();
            ready.Clear();
            foreach (var id in layer)
            {
                ordered.Add(byId[id]);
            }

            foreach (var id in layer)
            {
                if (!dependents.TryGetValue(id, out var dependentList))
                {
                    continue;
                }

                foreach (var dependentId in dependentList)
                {
                    indegrees[dependentId]--;
                    if (indegrees[dependentId] == 0)
                    {
                        ready.Add(dependentId);
                    }
                }
            }
        }

        return ordered.Count == items.Length ? ordered.ToImmutable() : null;
    }
    private static ExtensionGraphResult? ValidateContracts(
        ImmutableArray<ExtensionManifest> manifests,
        ExtensionContractCatalog? contractCatalog,
        Dictionary<string, string> providerIds,
        Dictionary<string, ExtensionContractExport> providerExports)
    {
        foreach (var manifest in manifests)
        {
            var exportIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var export in manifest.Exports)
            {
                if (!exportIds.Add(export.ContractId))
                {
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.DuplicateContractDeclaration,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' declares contract export '{export.ContractId}' more than once."));
                }

                if (!providerExports.TryAdd(export.ContractId, export))
                {
                    var existingProviderId = providerIds[export.ContractId];
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.DuplicateContractDeclaration,
                        new ExtensionErrorDetail(
                            $"Contract '{export.ContractId}' is exported by both extension '{existingProviderId}' and extension '{manifest.Id}'."));
                }

                providerIds.Add(export.ContractId, manifest.Id);

                if (contractCatalog is not null &&
                    contractCatalog.ValidateDeclaration(
                        manifest.ExtensionDirectory,
                        export.AssemblyIdentity,
                        export.TypeIdentity) != ExtensionFailureCode.None)
                {
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.ContractCatalogUnavailable,
                        new ExtensionErrorDetail(
                            $"The contract catalog rejected export '{export.ContractId}' declared by extension '{manifest.Id}'."));
                }
            }

            var importIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in manifest.Imports)
            {
                if (!importIds.Add(import.ContractId))
                {
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.DuplicateContractDeclaration,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' declares contract import '{import.ContractId}' more than once."));
                }

                if (contractCatalog is not null &&
                    contractCatalog.ValidateDeclaration(
                        manifest.ExtensionDirectory,
                        import.AssemblyIdentity,
                        import.TypeIdentity) != ExtensionFailureCode.None)
                {
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.ContractCatalogUnavailable,
                        new ExtensionErrorDetail(
                            $"The contract catalog rejected import '{import.ContractId}' declared by extension '{manifest.Id}'."));
                }
            }
        }

        foreach (var manifest in manifests)
        {
            foreach (var import in manifest.Imports)
            {
                if (!providerExports.TryGetValue(import.ContractId, out var provider))
                {
                    if (import.Optional)
                    {
                        continue;
                    }

                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.MissingContractProvider,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' requires contract '{import.ContractId}', but no extension exports it."));
                }

                if (!import.VersionRange.IsSatisfiedBy(provider.Version))
                {
                    if (import.Optional)
                    {
                        continue;
                    }

                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.ContractVersionIncompatible,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' requires contract '{import.ContractId}' in version range '{import.VersionRange}', but provider '{providerIds[import.ContractId]}' exports version '{provider.Version}'."));
                }

                if (!string.Equals(import.AssemblyIdentity, provider.AssemblyIdentity, StringComparison.Ordinal) ||
                    !string.Equals(import.TypeIdentity, provider.TypeIdentity, StringComparison.Ordinal))
                {
                    return ExtensionGraphResult.Failure(
                        ExtensionFailureCode.ContractIdentityMismatch,
                        new ExtensionErrorDetail(
                            $"Extension '{manifest.Id}' imports contract '{import.ContractId}' with an assembly or type identity that differs from its provider."));
                }
            }
        }

        return null;
    }
}
