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

    public ExtensionContractImportResult<TContract> TryImport<TContract>(string contractId)
        where TContract : class
    {
        if (!ExtensionIdentifierSyntax.IsValid(contractId))
        {
            return ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.InvalidArgument,
                new ExtensionErrorDetail(
                    $"The contractId argument {ExtensionDiagnosticText.Value(contractId)} is invalid; contract identifiers must be {ExtensionDiagnosticText.IdentifierSyntaxRule}."));
        }

        if (State == ExtensionDependencyState.Unavailable)
        {
            return ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.Unavailable,
                new ExtensionErrorDetail(
                    $"Contract import '{contractId}' is unavailable because dependency '{ExtensionId}' has state {State}."));
        }

        if (State != ExtensionDependencyState.Satisfied)
        {
            var installedVersion = InstalledVersion is null ? "not installed" : $"version '{InstalledVersion}'";
            var range = string.IsNullOrWhiteSpace(VersionRange) ? "no declared version range" : $"required range '{VersionRange}'";
            return ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.DependencyUnsatisfied,
                new ExtensionErrorDetail(
                    $"Dependency '{ExtensionId}' has state {State} with {installedVersion}; {range} is not satisfied, so contract '{contractId}' cannot be imported."));
        }

        if (_contracts is null)
        {
            return ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.Unavailable,
                new ExtensionErrorDetail(
                    $"Contract import '{contractId}' is unavailable because dependency '{ExtensionId}' has no contract registry."));
        }

        return _contracts.TryImport<TContract>(contractId);
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
