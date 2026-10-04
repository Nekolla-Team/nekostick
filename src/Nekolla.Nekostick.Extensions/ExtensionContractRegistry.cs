using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal enum ExtensionContractExportResolution
{
    Resolved,
    NotYetExported,
    TypeMismatch,
    RegistryUnavailable
}

internal readonly struct ExtensionContractProviderResolution
{
    private ExtensionContractProviderResolution(object? contract, ExtensionErrorDetail? reason)
    {
        Contract = contract;
        Reason = reason;
    }

    internal object? Contract { get; }

    internal ExtensionErrorDetail? Reason { get; }

    internal static ExtensionContractProviderResolution Success(object contract) =>
        new(contract, null);

    internal static ExtensionContractProviderResolution Failure(ExtensionErrorDetail reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new(null, reason);
    }
}

/// <summary>Owns one generation's startup-only typed shared-contract exchange.</summary>
internal sealed class ExtensionContractRegistry : IExtensionContractRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly ImmutableDictionary<string, ExtensionContractExport> _exports;
    private readonly ImmutableDictionary<string, ExtensionContractImport> _imports;
    private readonly Func<string, Type, SemVersionRange, ExtensionContractProviderResolution> _resolveProvider;
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private bool _startupOpen = true;
    private bool _disposed;

    internal ExtensionContractRegistry(
        ImmutableArray<ExtensionContractExport> exports,
        ImmutableArray<ExtensionContractImport> imports,
        Func<string, Type, SemVersionRange, ExtensionContractProviderResolution> resolveProvider)
    {
        _exports = exports.ToImmutableDictionary(static declaration => declaration.ContractId, StringComparer.Ordinal);
        _imports = imports.ToImmutableDictionary(static declaration => declaration.ContractId, StringComparer.Ordinal);
        _resolveProvider = resolveProvider;
    }

    public ExtensionContractExportResult TryExport<TContract>(string contractId, TContract implementation)
        where TContract : class
    {
        if (!ExtensionIdentifierSyntax.IsValid(contractId))
        {
            return ExtensionContractExportResult.Failure(
                ExtensionContractExportFailureCode.InvalidArgument,
                new ExtensionErrorDetail(
                    $"The contractId argument {ExtensionDiagnosticText.Value(contractId)} is invalid; contract identifiers must be {ExtensionDiagnosticText.IdentifierSyntaxRule}."));
        }

        if (implementation is null)
        {
            return ExtensionContractExportResult.Failure(
                ExtensionContractExportFailureCode.InvalidArgument,
                new ExtensionErrorDetail($"The implementation argument is null for contract {ExtensionDiagnosticText.Value(contractId)}."));
        }

        lock (_gate)
        {
            if (!_startupOpen || _disposed)
            {
                return ExtensionContractExportResult.Failure(
                    ExtensionContractExportFailureCode.Unavailable,
                    new ExtensionErrorDetail($"Contract startup exchange for '{contractId}' is closed or disposed."));
            }

            if (!_exports.TryGetValue(contractId, out var declaration))
            {
                return ExtensionContractExportResult.Failure(
                    ExtensionContractExportFailureCode.NotDeclared,
                    new ExtensionErrorDetail($"Contract '{contractId}' is not declared as an export."));
            }

            if (!TypeMatches<TContract>(declaration.TypeIdentity))
            {
                return ExtensionContractExportResult.Failure(
                    ExtensionContractExportFailureCode.TypeMismatch,
                    new ExtensionErrorDetail(
                        $"Implementation argument type '{typeof(TContract).FullName}' does not match declared type identity {ExtensionDiagnosticText.Value(declaration.TypeIdentity)} for contract '{contractId}'."));
            }

            if (_values.ContainsKey(contractId))
            {
                return ExtensionContractExportResult.Failure(
                    ExtensionContractExportFailureCode.Conflict,
                    new ExtensionErrorDetail($"An implementation for contract '{contractId}' has already been exported."));
            }

            _values.Add(contractId, implementation);
            return ExtensionContractExportResult.Success;
        }
    }

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

        // The provider resolution enters the runtime manager and provider registries; the registry
        // gate MUST NOT wrap that call (registry locks never wrap manager calls).
        ExtensionContractImport? declaration;
        lock (_gate)
        {
            if (!_startupOpen || _disposed)
            {
                return ExtensionContractImportResult<TContract>.Failure(
                    ExtensionContractImportFailureCode.Unavailable,
                    new ExtensionErrorDetail($"Contract startup exchange for '{contractId}' is closed or disposed."));
            }

            if (!_imports.TryGetValue(contractId, out declaration))
            {
                return ExtensionContractImportResult<TContract>.Failure(
                    ExtensionContractImportFailureCode.NotDeclared,
                    new ExtensionErrorDetail($"Contract '{contractId}' is not declared as an import."));
            }

            if (!TypeMatches<TContract>(declaration.TypeIdentity))
            {
                return ExtensionContractImportResult<TContract>.Failure(
                    ExtensionContractImportFailureCode.TypeMismatch,
                    new ExtensionErrorDetail(
                        $"Requested contract type '{typeof(TContract).FullName}' does not match declared type identity {ExtensionDiagnosticText.Value(declaration.TypeIdentity)} for contract '{contractId}'."));
            }

            // An import whose provider does not satisfy the declared range is unsatisfied even when
            // validation skipped it as optional; never hand out a contract outside the declared range.
            if (_exports.TryGetValue(contractId, out var ownExport) &&
                !declaration.VersionRange.IsSatisfiedBy(ownExport.Version))
            {
                return ExtensionContractImportResult<TContract>.Failure(
                    ExtensionContractImportFailureCode.VersionMismatch,
                    new ExtensionErrorDetail(
                        $"This extension exports contract '{contractId}' at version '{ownExport.Version}', outside import range '{declaration.VersionRange}'."));
            }

            if (_values.TryGetValue(contractId, out var ownValue) && ownValue is TContract ownContract)
            {
                return ExtensionContractImportResult<TContract>.Success(ownContract);
            }
        }

        var resolved = _resolveProvider(contractId, typeof(TContract), declaration.VersionRange);
        if (resolved.Contract is not TContract typed)
        {
            var reason = resolved.Reason ?? new ExtensionErrorDetail(
                $"Contract proxy resolution for '{contractId}' returned a value incompatible with expected type '{typeof(TContract).FullName}' in required range '{declaration.VersionRange}'.");
            return ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.ProviderUnavailable,
                reason);
        }

        return ExtensionContractImportResult<TContract>.Success(typed);
    }

    internal ExtensionContractExportResolution TryResolveExport(
        string contractId,
        Type contractType,
        out object? value)
    {
        value = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return ExtensionContractExportResolution.RegistryUnavailable;
            }

            if (!_values.TryGetValue(contractId, out var candidate))
            {
                return ExtensionContractExportResolution.NotYetExported;
            }

            value = candidate;
            return contractType.IsInstanceOfType(candidate)
                ? ExtensionContractExportResolution.Resolved
                : ExtensionContractExportResolution.TypeMismatch;
        }
    }

    internal void CompleteStartup()
    {
        lock (_gate)
        {
            _startupOpen = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _startupOpen = false;
            _values.Clear();
        }
    }

    private static bool TypeMatches<TContract>(string declaredTypeIdentity)
        where TContract : class =>
        string.Equals(
            typeof(TContract).FullName,
            declaredTypeIdentity,
            StringComparison.Ordinal);
}
