using System.Collections.Immutable;

namespace Nekolla.Nekostick.Extensions;

internal sealed record ManifestDependencyValues(string? Id, string? VersionRange);

internal sealed record ManifestDocumentValues(
    int? SchemaVersion,
    string? Id,
    string? Version,
    string? EntryAssembly,
    string? EntryType,
    IReadOnlyList<ManifestDependencyValues?>? Dependencies,
    string? RequiredHostApiVersion);

internal static class ManifestParserCore
{
    internal static ManifestDiscoveryResult Validate(
        string root,
        ManifestSourceFormat format,
        ManifestDocumentValues values)
    {
        if (values.SchemaVersion is null || values.Id is null || values.Version is null ||
            values.EntryAssembly is null || values.EntryType is null || values.Dependencies is null ||
            values.RequiredHostApiVersion is null || values.SchemaVersion != 1)
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
                root,
                entryAssemblyPath));
    }

    internal static ManifestDiscoveryResult Failure(ManifestSourceFormat format, ExtensionFailureCode code) =>
        ManifestDiscoveryResult.Failure(code, format);
}
