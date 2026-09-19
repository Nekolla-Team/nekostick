using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Resolves one declared dependency snapshot and its startup-only contract imports.</summary>
internal sealed class ExtensionDependencyContext : IExtensionDependencyContext
{
    private readonly ExtensionContractRegistry? _contracts;

    internal ExtensionDependencyContext(
        string extensionId,
        ExtensionDependencyState state,
        bool optional,
        string versionRange,
        string? installedVersion,
        ExtensionContractRegistry? contracts)
    {
        ExtensionId = extensionId;
        State = state;
        IsOptional = optional;
        VersionRange = versionRange;
        InstalledVersion = installedVersion;
        _contracts = contracts;
    }

    public string ExtensionId { get; }

    public ExtensionDependencyState State { get; }
    public bool IsOptional { get; }


    public string VersionRange { get; }

    public string? InstalledVersion { get; }

    public bool TryImport<TContract>(string contractId, out TContract? contract)
        where TContract : class
    {
        contract = null;
        return State == ExtensionDependencyState.Satisfied &&
            _contracts is not null &&
            _contracts.TryImport(contractId, out contract);
    }
}

/// <summary>Serves the dependency snapshots captured when the owning extension started.</summary>
internal sealed class ExtensionDependencyApi : IExtensionDependencyApi
{
    private readonly IReadOnlyDictionary<string, ExtensionDependencyContext> _contexts;
    private readonly ExtensionContractRegistry _contracts;

    internal ExtensionDependencyApi(
        IReadOnlyDictionary<string, ExtensionDependencyContext> contexts,
        ExtensionContractRegistry contracts)
    {
        _contexts = contexts;
        _contracts = contracts;
    }

    internal static ExtensionDependencyApi Create(
        ExtensionManifest manifest,
        IReadOnlyDictionary<string, SemVersion> availableVersions,
        ExtensionContractRegistry contracts)
    {
        var contexts = ImmutableDictionary.CreateBuilder<string, ExtensionDependencyContext>(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies)
        {
            var installed = availableVersions.TryGetValue(dependency.Id, out var version);
            var satisfied = installed && dependency.VersionRange.IsSatisfiedBy(version);
            contexts[dependency.Id] = new ExtensionDependencyContext(
                dependency.Id,
                !installed
                    ? ExtensionDependencyState.NotInstalled
                    : satisfied
                        ? ExtensionDependencyState.Satisfied
                        : ExtensionDependencyState.VersionMismatch,
                dependency.Optional,
                dependency.VersionRange.ToString(),
                installed ? version.ToString() : null,
                contracts);
        }

        return new ExtensionDependencyApi(contexts.ToImmutable(), contracts);
    }

    public IExtensionDependencyContext GetDependencyContext(string extensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        return _contexts.TryGetValue(extensionId, out var context)
            ? context
            : new ExtensionDependencyContext(
                extensionId,
                ExtensionDependencyState.NotDeclared,
                optional: false,
                versionRange: string.Empty,
                installedVersion: null,
                contracts: null);
    }
}
