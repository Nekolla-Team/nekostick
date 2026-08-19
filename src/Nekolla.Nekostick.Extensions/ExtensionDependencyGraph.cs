using System.Collections.Immutable;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Validates dependencies and produces a deterministic topological order.</summary>
public static class ExtensionManifestGraph
{
    /// <summary>Validates an explicitly supplied manifest set.</summary>
    /// <param name="manifests">The manifests already discovered by the caller.</param>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    /// <param name="contractCatalog">The immutable host-owned shared contract catalog.</param>
    /// <returns>A graph result whose layers are ordinal ID sorted.</returns>
    public static ExtensionGraphResult ValidateAndOrder(
        IEnumerable<ExtensionManifest>? manifests,
        SemVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null)
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
        catch (Exception)
        {
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
        var contractFailure = ValidateContracts(items, contractCatalog, contractProviders);
        if (contractFailure != ExtensionFailureCode.None)
        {
            return ExtensionGraphResult.Failure(contractFailure);
        }

        var indegrees = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var manifest in items)
        {
            var edgeTargets = new HashSet<string>(StringComparer.Ordinal);
            indegrees[manifest.Id] = 0;

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
                    return ExtensionGraphResult.Failure(ExtensionFailureCode.MissingDependency);
                }

                if (!dependency.VersionRange.IsSatisfiedBy(dependencyManifest.Version))
                {
                    return ExtensionGraphResult.Failure(ExtensionFailureCode.DependencyVersionIncompatible);
                }

                if (!edgeTargets.Add(dependency.Id))
                {
                    continue;
                }

                indegrees[manifest.Id]++;
                if (!dependents.TryGetValue(dependency.Id, out var dependentList))
                {
                    dependentList = new List<string>();
                    dependents.Add(dependency.Id, dependentList);
                }

                dependentList.Add(manifest.Id);
            }

            foreach (var import in manifest.Imports)
            {
                var providerId = contractProviders[import.ContractId];
                if (string.Equals(providerId, manifest.Id, StringComparison.Ordinal) ||
                    !edgeTargets.Add(providerId))
                {
                    continue;
                }

                indegrees[manifest.Id]++;
                if (!dependents.TryGetValue(providerId, out var dependentList))
                {
                    dependentList = new List<string>();
                    dependents.Add(providerId, dependentList);
                }

                dependentList.Add(manifest.Id);
            }
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

        return ordered.Count == items.Length
            ? ExtensionGraphResult.Success(ordered.ToImmutable())
            : ExtensionGraphResult.Failure(ExtensionFailureCode.DependencyCycle);
    }
    private static ExtensionFailureCode ValidateContracts(
        ImmutableArray<ExtensionManifest> manifests,
        ExtensionContractCatalog? contractCatalog,
        Dictionary<string, string> providerIds)
    {
        var providers = new Dictionary<string, ExtensionContractExport>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            var exportIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var export in manifest.Exports)
            {
                if (!exportIds.Add(export.ContractId) || !providers.TryAdd(export.ContractId, export))
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
                if (!providers.TryGetValue(import.ContractId, out var provider))
                {
                    return ExtensionFailureCode.MissingContractProvider;
                }

                if (!import.VersionRange.IsSatisfiedBy(provider.Version))
                {
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
