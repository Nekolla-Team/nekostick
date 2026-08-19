using System.Collections.Immutable;

namespace Nekolla.Nekostick.Extensions;

internal sealed record ManifestDependencyValues(string? Id, string? VersionRange);

internal sealed record ManifestContractExportValues(
    string? ContractId,
    string? Version,
    string? AssemblyIdentity,
    string? TypeIdentity);

internal sealed record ManifestContractImportValues(
    string? ContractId,
    string? VersionRange,
    string? AssemblyIdentity,
    string? TypeIdentity);

internal sealed record ManifestDocumentValues(
    int? SchemaVersion,
    string? Id,
    string? Version,
    string? EntryAssembly,
    string? EntryType,
    IReadOnlyList<ManifestDependencyValues?>? Dependencies,
    string? RequiredHostApiVersion,
    IReadOnlyList<ManifestContractExportValues?>? Exports,
    IReadOnlyList<ManifestContractImportValues?>? Imports);

internal static class ManifestParserCore
{
    internal static ManifestDiscoveryResult Validate(
        string root,
        ManifestSourceFormat format,
        ManifestDocumentValues values)
    {
        if (values.SchemaVersion is null || values.Id is null || values.Version is null ||
            values.EntryAssembly is null || values.EntryType is null || values.Dependencies is null ||
            values.RequiredHostApiVersion is null || values.Exports is null || values.Imports is null ||
            values.SchemaVersion != 1)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid);
        }

        if (!ExtensionIdentifierSyntax.IsValid(values.Id))
        {
            return Failure(format, ExtensionFailureCode.InvalidIdentifier);
        }

        if (!SemVersion.TryParse(values.Version, out var version))
        {
            return Failure(format, ExtensionFailureCode.InvalidVersion);
        }

        if (!SemVersionRange.TryParse(values.RequiredHostApiVersion, out var hostRange) || hostRange is null)
        {
            return Failure(format, ExtensionFailureCode.InvalidVersionRange);
        }

        if (!ManifestNameSyntax.IsValidEntryAssembly(values.EntryAssembly) ||
            !ManifestNameSyntax.IsValidEntryType(values.EntryType))
        {
            return Failure(format, ExtensionFailureCode.UnsafePath);
        }

        var declaredEntryPath = Path.Combine(
            root,
            values.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(declaredEntryPath))
        {
            return Failure(format, ExtensionFailureCode.EntryAssemblyMissing);
        }

        if (!CanonicalPath.TryCanonicalFileInRoot(root, declaredEntryPath, out var entryAssemblyPath))
        {
            return Failure(format, ExtensionFailureCode.UnsafePath);
        }

        var dependencies = ImmutableArray.CreateBuilder<ExtensionDependency>(values.Dependencies.Count);
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in values.Dependencies)
        {
            if (dependency is null || !ExtensionIdentifierSyntax.IsValid(dependency.Id))
            {
                return Failure(format, ExtensionFailureCode.InvalidIdentifier);
            }

            if (!dependencyIds.Add(dependency.Id!))
            {
                return Failure(format, ExtensionFailureCode.DuplicateExtensionId);
            }

            if (!SemVersionRange.TryParse(dependency.VersionRange, out var dependencyRange) || dependencyRange is null)
            {
                return Failure(format, ExtensionFailureCode.InvalidVersionRange);
            }

            dependencies.Add(new ExtensionDependency(dependency.Id!, dependencyRange));
        }
        var exports = ImmutableArray.CreateBuilder<ExtensionContractExport>(values.Exports.Count);
        var exportIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in values.Exports)
        {
            if (export is null || !ExtensionIdentifierSyntax.IsValid(export.ContractId))
            {
                return Failure(format, ExtensionFailureCode.InvalidIdentifier);
            }

            if (!exportIds.Add(export.ContractId!))
            {
                return Failure(format, ExtensionFailureCode.DuplicateContractDeclaration);
            }

            if (!SemVersion.TryParse(export.Version, out var exportVersion))
            {
                return Failure(format, ExtensionFailureCode.InvalidVersion);
            }

            if (string.IsNullOrWhiteSpace(export.AssemblyIdentity) ||
                string.IsNullOrWhiteSpace(export.TypeIdentity))
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid);
            }

            exports.Add(new ExtensionContractExport(
                export.ContractId!,
                exportVersion,
                export.AssemblyIdentity!,
                export.TypeIdentity!));
        }

        var imports = ImmutableArray.CreateBuilder<ExtensionContractImport>(values.Imports.Count);
        var importIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in values.Imports)
        {
            if (import is null || !ExtensionIdentifierSyntax.IsValid(import.ContractId))
            {
                return Failure(format, ExtensionFailureCode.InvalidIdentifier);
            }

            if (!importIds.Add(import.ContractId!))
            {
                return Failure(format, ExtensionFailureCode.DuplicateContractDeclaration);
            }

            if (!SemVersionRange.TryParse(import.VersionRange, out var importRange) || importRange is null)
            {
                return Failure(format, ExtensionFailureCode.InvalidVersionRange);
            }

            if (string.IsNullOrWhiteSpace(import.AssemblyIdentity) ||
                string.IsNullOrWhiteSpace(import.TypeIdentity))
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid);
            }

            imports.Add(new ExtensionContractImport(
                import.ContractId!,
                importRange,
                import.AssemblyIdentity!,
                import.TypeIdentity!));
        }

        return ManifestDiscoveryResult.Success(
            format,
            new ExtensionManifest(
                values.SchemaVersion.Value,
                values.Id,
                version,
                values.EntryAssembly,
                values.EntryType,
                hostRange,
                dependencies.ToImmutable(),
                exports.ToImmutable(),
                imports.ToImmutable(),
                root,
                entryAssemblyPath));
    }

    internal static ManifestDiscoveryResult Failure(ManifestSourceFormat format, ExtensionFailureCode code) =>
        ManifestDiscoveryResult.Failure(code, format);
}
