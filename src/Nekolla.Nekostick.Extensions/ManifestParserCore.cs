using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal sealed record ManifestDependencyValues(string? Id, string? VersionRange, bool Optional);

internal sealed record ManifestContractExportValues(
    string? ContractId,
    string? Version,
    string? AssemblyIdentity,
    string? TypeIdentity);

internal sealed record ManifestContractImportValues(
    string? ContractId,
    string? VersionRange,
    string? AssemblyIdentity,
    string? TypeIdentity,
    bool Optional);

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
        if (values.SchemaVersion is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'schemaVersion' is missing or is not a 32-bit integer.");
        }

        if (values.SchemaVersion != 1)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                $"Root field 'schemaVersion' value {ExtensionDiagnosticText.Value(values.SchemaVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))} is unsupported; expected '1'.");
        }

        if (values.Id is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'id' is missing or is not a string.");
        }

        if (!ExtensionIdentifierSyntax.IsValid(values.Id))
        {
            return Failure(format, ExtensionFailureCode.InvalidIdentifier,
                $"Root field 'id' value {ExtensionDiagnosticText.Value(values.Id)} is not a valid extension identifier; identifiers must be {ExtensionDiagnosticText.IdentifierSyntaxRule}.");
        }

        if (values.Version is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'version' is missing or is not a string.");
        }

        if (!SemVersion.TryParse(values.Version, out var version))
        {
            return Failure(format, ExtensionFailureCode.InvalidVersion,
                $"Root field 'version' value {ExtensionDiagnosticText.Value(values.Version)} is not a valid semantic version.");
        }

        if (values.RequiredHostApiVersion is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'requiredHostApiVersion' is missing or is not a string.");
        }

        if (!SemVersionRange.TryParse(values.RequiredHostApiVersion, out var hostRange) || hostRange is null)
        {
            return Failure(format, ExtensionFailureCode.InvalidVersionRange,
                $"Root field 'requiredHostApiVersion' value {ExtensionDiagnosticText.Value(values.RequiredHostApiVersion)} is not a valid semantic version range.");
        }

        if (values.EntryAssembly is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'entryAssembly' is missing or is not a string.");
        }

        if (!ManifestNameSyntax.IsValidEntryAssembly(values.EntryAssembly))
        {
            return Failure(format, ExtensionFailureCode.UnsafePath,
                $"Root field 'entryAssembly' value {ExtensionDiagnosticText.Value(values.EntryAssembly)} is unsafe; it must be a relative path of at most 512 characters ending in '.dll', use '/' separators, and contain no empty, '.', or '..' segments or control characters.");
        }

        if (values.EntryType is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'entryType' is missing or is not a string.");
        }

        if (!ManifestNameSyntax.IsValidEntryType(values.EntryType))
        {
            return Failure(format, ExtensionFailureCode.UnsafePath,
                $"Root field 'entryType' value {ExtensionDiagnosticText.Value(values.EntryType)} is unsafe; it must be a qualified type name of at most 512 characters with non-empty identifier segments separated by '.' or '+'.");
        }

        if (values.Dependencies is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Required root field 'dependencies' is missing or is not a list.");
        }

        if (values.Exports is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Root field 'exports' value <null> could not be read as a declaration list.");
        }

        if (values.Imports is null)
        {
            return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                "Root field 'imports' value <null> could not be read as a declaration list.");
        }

        var declaredEntryPath = Path.Combine(
            root,
            values.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(declaredEntryPath))
        {
            return Failure(format, ExtensionFailureCode.EntryAssemblyMissing,
                $"Root field 'entryAssembly' value {ExtensionDiagnosticText.Value(values.EntryAssembly)} does not identify an existing file inside the extension directory.");
        }

        if (!CanonicalPath.TryCanonicalFileInRoot(root, declaredEntryPath, out var entryAssemblyPath))
        {
            return Failure(format, ExtensionFailureCode.UnsafePath,
                $"Root field 'entryAssembly' value {ExtensionDiagnosticText.Value(values.EntryAssembly)} resolves outside the extension directory.");
        }

        var dependencies = ImmutableArray.CreateBuilder<ExtensionDependency>(values.Dependencies.Count);
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Dependencies.Count; index++)
        {
            var dependency = values.Dependencies[index];
            var path = $"dependencies[{index}]";
            if (dependency is null)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Manifest field '{path}' is null; expected a dependency object.");
            }

            if (!ExtensionIdentifierSyntax.IsValid(dependency.Id))
            {
                return Failure(format, ExtensionFailureCode.InvalidIdentifier,
                    $"Field '{path}.id' value {ExtensionDiagnosticText.Value(dependency.Id)} is not a valid extension identifier; identifiers must be {ExtensionDiagnosticText.IdentifierSyntaxRule}.");
            }

            if (!dependencyIds.Add(dependency.Id!))
            {
                return Failure(format, ExtensionFailureCode.DuplicateExtensionId,
                    $"Field '{path}.id' value {ExtensionDiagnosticText.Value(dependency.Id)} duplicates an earlier dependency identifier.");
            }

            if (!SemVersionRange.TryParse(dependency.VersionRange, out var dependencyRange) || dependencyRange is null)
            {
                return Failure(format, ExtensionFailureCode.InvalidVersionRange,
                    $"Field '{path}.versionRange' value {ExtensionDiagnosticText.Value(dependency.VersionRange)} is not a valid semantic version range.");
            }

            dependencies.Add(new ExtensionDependency(dependency.Id!, dependencyRange, dependency.Optional));
        }

        var exports = ImmutableArray.CreateBuilder<ExtensionContractExport>(values.Exports.Count);
        var exportIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Exports.Count; index++)
        {
            var export = values.Exports[index];
            var path = $"exports[{index}]";
            if (export is null)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Manifest field '{path}' is null; expected an export object.");
            }

            if (!ExtensionIdentifierSyntax.IsValid(export.ContractId))
            {
                return Failure(format, ExtensionFailureCode.InvalidIdentifier,
                    $"Field '{path}.contractId' value {ExtensionDiagnosticText.Value(export.ContractId)} is not a valid contract identifier; identifiers must be {ExtensionDiagnosticText.IdentifierSyntaxRule}.");
            }

            if (!exportIds.Add(export.ContractId!))
            {
                return Failure(format, ExtensionFailureCode.DuplicateContractDeclaration,
                    $"Field '{path}.contractId' value {ExtensionDiagnosticText.Value(export.ContractId)} duplicates an earlier contract declaration.");
            }

            if (!SemVersion.TryParse(export.Version, out var exportVersion))
            {
                return Failure(format, ExtensionFailureCode.InvalidVersion,
                    $"Field '{path}.version' value {ExtensionDiagnosticText.Value(export.Version)} is not a valid semantic version.");
            }

            if (string.IsNullOrWhiteSpace(export.AssemblyIdentity) || export.AssemblyIdentity.Length > 1024)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Field '{path}.assemblyIdentity' value {ExtensionDiagnosticText.Value(export.AssemblyIdentity)} must be non-empty and at most 1024 characters.");
            }

            if (string.IsNullOrWhiteSpace(export.TypeIdentity) || export.TypeIdentity.Length > 1024)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Field '{path}.typeIdentity' value {ExtensionDiagnosticText.Value(export.TypeIdentity)} must be non-empty and at most 1024 characters.");
            }

            exports.Add(new ExtensionContractExport(
                export.ContractId!,
                exportVersion,
                export.AssemblyIdentity!,
                export.TypeIdentity!));
        }

        var imports = ImmutableArray.CreateBuilder<ExtensionContractImport>(values.Imports.Count);
        var importIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Imports.Count; index++)
        {
            var import = values.Imports[index];
            var path = $"imports[{index}]";
            if (import is null)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Manifest field '{path}' is null; expected an import object.");
            }

            if (!ExtensionIdentifierSyntax.IsValid(import.ContractId))
            {
                return Failure(format, ExtensionFailureCode.InvalidIdentifier,
                    $"Field '{path}.contractId' value {ExtensionDiagnosticText.Value(import.ContractId)} is not a valid contract identifier; identifiers must be {ExtensionDiagnosticText.IdentifierSyntaxRule}.");
            }

            if (!importIds.Add(import.ContractId!))
            {
                return Failure(format, ExtensionFailureCode.DuplicateContractDeclaration,
                    $"Field '{path}.contractId' value {ExtensionDiagnosticText.Value(import.ContractId)} duplicates an earlier contract declaration.");
            }

            if (!SemVersionRange.TryParse(import.VersionRange, out var importRange) || importRange is null)
            {
                return Failure(format, ExtensionFailureCode.InvalidVersionRange,
                    $"Field '{path}.versionRange' value {ExtensionDiagnosticText.Value(import.VersionRange)} is not a valid semantic version range.");
            }

            if (string.IsNullOrWhiteSpace(import.AssemblyIdentity) || import.AssemblyIdentity.Length > 1024)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Field '{path}.assemblyIdentity' value {ExtensionDiagnosticText.Value(import.AssemblyIdentity)} must be non-empty and at most 1024 characters.");
            }

            if (string.IsNullOrWhiteSpace(import.TypeIdentity) || import.TypeIdentity.Length > 1024)
            {
                return Failure(format, ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Field '{path}.typeIdentity' value {ExtensionDiagnosticText.Value(import.TypeIdentity)} must be non-empty and at most 1024 characters.");
            }

            imports.Add(new ExtensionContractImport(
                import.ContractId!,
                importRange,
                import.AssemblyIdentity!,
                import.TypeIdentity!,
                import.Optional));
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

    internal static ManifestDiscoveryResult Failure(
        ManifestSourceFormat format,
        ExtensionFailureCode code,
        string message) =>
        Failure(format, code, new ExtensionErrorDetail(message));

    internal static ManifestDiscoveryResult Failure(
        ManifestSourceFormat format,
        ExtensionFailureCode code,
        ExtensionErrorDetail detail) =>
        ManifestDiscoveryResult.Failure(code, detail, format);
}
