using System.Collections.Immutable;
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
            return ExtensionGraphResult.Failure(ExtensionFailureCode.InvalidArgument);
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

            return ExtensionGraphResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        var byId = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        foreach (var manifest in items)
        {
            if (manifest is null || !ExtensionIdentifierSyntax.IsValid(manifest.Id))
            {
                return ExtensionGraphResult.Failure(ExtensionFailureCode.InvalidIdentifier);
            }

            if (!byId.TryAdd(manifest.Id, manifest))
            {
                return ExtensionGraphResult.Failure(ExtensionFailureCode.DuplicateExtensionId);
            }

            if (!manifest.RequiredHostApiVersion.IsSatisfiedBy(hostApiVersion))
            {
                return ExtensionGraphResult.Failure(ExtensionFailureCode.HostApiIncompatible);
            }
        }
        var contractProviders = new Dictionary<string, string>(StringComparer.Ordinal);
        var contractExports = new Dictionary<string, ExtensionContractExport>(StringComparer.Ordinal);
        var contractFailure = ValidateContracts(items, contractCatalog, contractProviders, contractExports);
        if (contractFailure != ExtensionFailureCode.None)
        {
            return ExtensionGraphResult.Failure(contractFailure);
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
                    return ExtensionGraphResult.Failure(ExtensionFailureCode.DuplicateExtensionId);
                }

                if (!byId.TryGetValue(dependency.Id, out var dependencyManifest))
                {
                    if (dependency.Optional)
                    {
                        continue;
                    }

                    return ExtensionGraphResult.Failure(ExtensionFailureCode.MissingDependency);
                }

                if (!dependency.VersionRange.IsSatisfiedBy(dependencyManifest.Version))
                {
                    if (dependency.Optional)
                    {
                        continue;
                    }

                    return ExtensionGraphResult.Failure(ExtensionFailureCode.DependencyVersionIncompatible);
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
            : ExtensionGraphResult.Failure(ExtensionFailureCode.DependencyCycle);
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
    private static ExtensionFailureCode ValidateContracts(
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
                if (!exportIds.Add(export.ContractId) || !providerExports.TryAdd(export.ContractId, export))
                {
                    return ExtensionFailureCode.DuplicateContractDeclaration;
                }
                providerIds.Add(export.ContractId, manifest.Id);

                if (contractCatalog is not null &&
                    contractCatalog.ValidateDeclaration(
                        manifest.ExtensionDirectory,
                        export.AssemblyIdentity,
                        export.TypeIdentity) != ExtensionFailureCode.None)
                {
                    return ExtensionFailureCode.ContractCatalogUnavailable;
                }
            }

            var importIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in manifest.Imports)
            {
                if (!importIds.Add(import.ContractId))
                {
                    return ExtensionFailureCode.DuplicateContractDeclaration;
                }

                if (contractCatalog is not null &&
                    contractCatalog.ValidateDeclaration(
                        manifest.ExtensionDirectory,
                        import.AssemblyIdentity,
                        import.TypeIdentity) != ExtensionFailureCode.None)
                {
                    return ExtensionFailureCode.ContractCatalogUnavailable;
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

                    return ExtensionFailureCode.MissingContractProvider;
                }

                if (!import.VersionRange.IsSatisfiedBy(provider.Version))
                {
                    if (import.Optional)
                    {
                        continue;
                    }

                    return ExtensionFailureCode.ContractVersionIncompatible;
                }

                if (!string.Equals(import.AssemblyIdentity, provider.AssemblyIdentity, StringComparison.Ordinal) ||
                    !string.Equals(import.TypeIdentity, provider.TypeIdentity, StringComparison.Ordinal))
                {
                    return ExtensionFailureCode.ContractIdentityMismatch;
                }
            }
        }

        return ExtensionFailureCode.None;
    }
}
