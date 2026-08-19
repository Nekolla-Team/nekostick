using System.Text.Json;

namespace Nekolla.Nekostick.Extensions;

internal static class JsonManifestParser
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxManifestDepth = 32;

    internal static ManifestDiscoveryResult Parse(string root, string manifestPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > MaxManifestBytes)
            {
                return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.JsonInvalid);
            }

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxManifestDepth
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.JsonInvalid);
            }

            var rootFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!rootFields.Add(property.Name))
                {
                    return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.DuplicateManifestField);
                }
            }

            if (!rootFields.IsSubsetOf(ManifestSchema.AllowedFields))
            {
                return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.UnknownManifestField);
            }

            if (!rootFields.IsSupersetOf(ManifestSchema.RequiredFields))
            {
                return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.ManifestSchemaInvalid);
            }

            var rootElement = document.RootElement;
            if (!TryGetInt(rootElement, "schemaVersion", out var schemaVersion) ||
                !TryGetString(rootElement, "id", out var id) ||
                !TryGetString(rootElement, "version", out var version) ||
                !TryGetString(rootElement, "entryAssembly", out var entryAssembly) ||
                !TryGetString(rootElement, "entryType", out var entryType) ||
                !TryGetString(rootElement, "requiredHostApiVersion", out var hostApiVersion) ||
                !rootElement.TryGetProperty("dependencies", out var dependenciesElement) ||
                dependenciesElement.ValueKind != JsonValueKind.Array)
            {
                return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.ManifestSchemaInvalid);
            }

            var dependencies = new List<ManifestDependencyValues?>();
            foreach (var dependencyElement in dependenciesElement.EnumerateArray())
            {
                if (dependencyElement.ValueKind != JsonValueKind.Object)
                {
                    return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.ManifestSchemaInvalid);
                }

                var dependencyFields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in dependencyElement.EnumerateObject())
                {
                    if (!dependencyFields.Add(property.Name))
                    {
                        return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.DuplicateManifestField);
                    }
                }

                if (!dependencyFields.IsSubsetOf(ManifestSchema.DependencyFields))
                {
                    return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.UnknownManifestField);
                }

                if (!dependencyFields.SetEquals(ManifestSchema.DependencyFields) ||
                    !TryGetString(dependencyElement, "id", out var dependencyId) ||
                    !TryGetString(dependencyElement, "versionRange", out var dependencyRange))
                {
                    return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.ManifestSchemaInvalid);
                }

                dependencies.Add(new ManifestDependencyValues(dependencyId, dependencyRange));
            }

            var exportsValid = TryGetExports(rootElement, out var exports, out var exportFailure);
            var importsValid = TryGetImports(rootElement, out var imports, out var importFailure);
            if (!exportsValid || !importsValid)
            {
                return ManifestParserCore.Failure(
                    ManifestSourceFormat.Json,
                    exportFailure != ExtensionFailureCode.None ? exportFailure : importFailure);
            }

            return ManifestParserCore.Validate(
                root,
                ManifestSourceFormat.Json,
                new ManifestDocumentValues(
                    schemaVersion,
                    id,
                    version,
                    entryAssembly,
                    entryType,
                    dependencies,
                    hostApiVersion,
                    exports,
                    imports));
        }

        catch (JsonException)
        {
            return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.JsonInvalid);
        }
        catch (Exception)
        {
            return ManifestParserCore.Failure(ManifestSourceFormat.Json, ExtensionFailureCode.LoadFailed);
        }
    }
    private static bool TryGetExports(
        JsonElement root,
        out List<ManifestContractExportValues> exports,
        out ExtensionFailureCode failure)
    {
        exports = new List<ManifestContractExportValues>();
        failure = ExtensionFailureCode.None;
        if (!root.TryGetProperty("exports", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            failure = ExtensionFailureCode.ManifestSchemaInvalid;
            return false;
        }

        foreach (var declaration in element.EnumerateArray())
        {
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            var fields = declaration.EnumerateObject().ToArray();
            var names = fields.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count != fields.Length)
            {
                failure = ExtensionFailureCode.DuplicateManifestField;
                return false;
            }

            if (!names.SetEquals(ManifestSchema.ExportFields) ||
                !TryGetString(declaration, "contractId", out var id) ||
                !TryGetString(declaration, "version", out var version) ||
                !TryGetString(declaration, "assemblyIdentity", out var assemblyIdentity) ||
                !TryGetString(declaration, "typeIdentity", out var typeIdentity))
            {
                failure = names.IsSubsetOf(ManifestSchema.ExportFields)
                    ? ExtensionFailureCode.ManifestSchemaInvalid
                    : ExtensionFailureCode.UnknownManifestField;
                return false;
            }

            exports.Add(new ManifestContractExportValues(id, version, assemblyIdentity, typeIdentity));
        }

        return true;
    }

    private static bool TryGetImports(
        JsonElement root,
        out List<ManifestContractImportValues> imports,
        out ExtensionFailureCode failure)
    {
        imports = new List<ManifestContractImportValues>();
        failure = ExtensionFailureCode.None;
        if (!root.TryGetProperty("imports", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            failure = ExtensionFailureCode.ManifestSchemaInvalid;
            return false;
        }

        foreach (var declaration in element.EnumerateArray())
        {
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            var fields = declaration.EnumerateObject().ToArray();
            var names = fields.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count != fields.Length)
            {
                failure = ExtensionFailureCode.DuplicateManifestField;
                return false;
            }

            if (!names.SetEquals(ManifestSchema.ImportFields) ||
                !TryGetString(declaration, "contractId", out var id) ||
                !TryGetString(declaration, "versionRange", out var versionRange) ||
                !TryGetString(declaration, "assemblyIdentity", out var assemblyIdentity) ||
                !TryGetString(declaration, "typeIdentity", out var typeIdentity))
            {
                failure = names.IsSubsetOf(ManifestSchema.ImportFields)
                    ? ExtensionFailureCode.ManifestSchemaInvalid
                    : ExtensionFailureCode.UnknownManifestField;
                return false;
            }

            imports.Add(new ManifestContractImportValues(id, versionRange, assemblyIdentity, typeIdentity));
        }

        return true;
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        return root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            (value = property.GetString()) is not null;
    }

    private static bool TryGetInt(JsonElement root, string name, out int? value)
    {
        value = null;
        return root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var parsed) &&
            (value = parsed) is not null;
    }
}

internal static class ManifestSchema
{
    internal static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "schemaVersion",
        "id",
        "version",
        "entryAssembly",
        "entryType",
        "dependencies",
        "requiredHostApiVersion",
        "exports",
        "imports"
    };
    internal static readonly IReadOnlySet<string> RequiredFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "schemaVersion",
        "id",
        "version",
        "entryAssembly",
        "entryType",
        "dependencies",
        "requiredHostApiVersion"
    };

    internal static readonly IReadOnlySet<string> DependencyFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "id",
        "versionRange"
    };
    internal static readonly IReadOnlySet<string> ExportFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "contractId",
        "version",
        "assemblyIdentity",
        "typeIdentity"
    };

    internal static readonly IReadOnlySet<string> ImportFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "contractId",
        "versionRange",
        "assemblyIdentity",
        "typeIdentity"
    };
}
