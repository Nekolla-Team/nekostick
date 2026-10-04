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
                return Failure(
                    ExtensionFailureCode.JsonInvalid,
                    $"JSON manifest {ExtensionDiagnosticText.Value(manifestPath)} is {bytes.Length} bytes; the maximum supported size is {MaxManifestBytes} bytes.");
            }

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxManifestDepth
                });
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    ExtensionFailureCode.JsonInvalid,
                    $"JSON manifest root value {DescribeValue(rootElement)} must be an object.");
            }

            if (!TryValidateObjectFields(
                    rootElement,
                    ManifestSchema.AllowedFields,
                    "root",
                    out var rootFields,
                    out var fieldFailure,
                    out var fieldFailureMessage))
            {
                return Failure(fieldFailure, fieldFailureMessage);
            }

            if (!TryValidateRequiredFields(rootFields, ManifestSchema.RequiredFields, "root", out var missingMessage))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, missingMessage);
            }

            if (!TryGetInt(rootElement, "schemaVersion", "root", out var schemaVersion, out var failureMessage))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, failureMessage);
            }

            if (!TryGetString(rootElement, "id", "root", out var id, out failureMessage) ||
                !TryGetString(rootElement, "version", "root", out var version, out failureMessage) ||
                !TryGetString(rootElement, "entryAssembly", "root", out var entryAssembly, out failureMessage) ||
                !TryGetString(rootElement, "entryType", "root", out var entryType, out failureMessage) ||
                !TryGetString(rootElement, "requiredHostApiVersion", "root", out var hostApiVersion, out failureMessage))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, failureMessage);
            }

            if (!rootElement.TryGetProperty("dependencies", out var dependenciesElement))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, "Required field 'dependencies' at root is missing.");
            }

            if (dependenciesElement.ValueKind != JsonValueKind.Array)
            {
                return Failure(
                    ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Field 'root.dependencies' value {DescribeValue(dependenciesElement)} must be a JSON array.");
            }

            var dependencies = new List<ManifestDependencyValues?>();
            var dependencyIndex = 0;
            foreach (var dependencyElement in dependenciesElement.EnumerateArray())
            {
                var path = $"dependencies[{dependencyIndex}]";
                dependencyIndex++;
                if (dependencyElement.ValueKind != JsonValueKind.Object)
                {
                    return Failure(
                        ExtensionFailureCode.ManifestSchemaInvalid,
                        $"Field '{path}' value {DescribeValue(dependencyElement)} must be a dependency object.");
                }

                if (!TryValidateObjectFields(
                        dependencyElement,
                        ManifestSchema.DependencyFields,
                        path,
                        out var dependencyFields,
                        out fieldFailure,
                        out fieldFailureMessage))
                {
                    return Failure(fieldFailure, fieldFailureMessage);
                }

                if (!TryValidateRequiredFields(
                        dependencyFields,
                        ManifestSchema.DependencyRequiredFields,
                        path,
                        out missingMessage))
                {
                    return Failure(ExtensionFailureCode.ManifestSchemaInvalid, missingMessage);
                }

                if (!TryGetString(dependencyElement, "id", path, out var dependencyId, out failureMessage) ||
                    !TryGetString(dependencyElement, "versionRange", path, out var dependencyRange, out failureMessage) ||
                    !TryGetOptionalBool(dependencyElement, "optional", path, out var dependencyOptional, out failureMessage))
                {
                    return Failure(ExtensionFailureCode.ManifestSchemaInvalid, failureMessage);
                }

                dependencies.Add(new ManifestDependencyValues(dependencyId, dependencyRange, dependencyOptional));
            }

            var exportsValid = TryGetExports(rootElement, out var exports, out var exportFailure, out var exportFailureMessage);
            var importsValid = TryGetImports(rootElement, out var imports, out var importFailure, out var importFailureMessage);
            if (!exportsValid || !importsValid)
            {
                return Failure(
                    exportFailure != ExtensionFailureCode.None ? exportFailure : importFailure,
                    exportsValid ? importFailureMessage : exportFailureMessage);
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
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var column = (exception.BytePositionInLine ?? 0) + 1;
            return Failure(
                ExtensionFailureCode.JsonInvalid,
                $"JSON manifest syntax is invalid at line {line}, column {column}: {ExtensionDiagnosticText.Value(exception.Message)}.");
        }
        catch (Exception exception)
        {
            return Failure(
                ExtensionFailureCode.LoadFailed,
                $"Could not read JSON manifest {ExtensionDiagnosticText.Value(manifestPath)} ({exception.GetType().Name}): {ExtensionDiagnosticText.Value(exception.Message)}.");
        }
    }

    private static bool TryGetExports(
        JsonElement root,
        out List<ManifestContractExportValues> exports,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        exports = new List<ManifestContractExportValues>();
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        if (!root.TryGetProperty("exports", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            failure = ExtensionFailureCode.ManifestSchemaInvalid;
            failureMessage = $"Field 'root.exports' value {DescribeValue(element)} must be a JSON array.";
            return false;
        }

        var index = 0;
        foreach (var declaration in element.EnumerateArray())
        {
            var path = $"exports[{index}]";
            index++;
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                failureMessage = $"Field '{path}' value {DescribeValue(declaration)} must be an export object.";
                return false;
            }

            if (!TryValidateObjectFields(
                    declaration,
                    ManifestSchema.ExportFields,
                    path,
                    out var fields,
                    out failure,
                    out failureMessage))
            {
                return false;
            }

            if (!TryValidateRequiredFields(fields, ManifestSchema.ExportFields, path, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            if (!TryGetString(declaration, "contractId", path, out var id, out failureMessage) ||
                !TryGetString(declaration, "version", path, out var version, out failureMessage) ||
                !TryGetString(declaration, "assemblyIdentity", path, out var assemblyIdentity, out failureMessage) ||
                !TryGetString(declaration, "typeIdentity", path, out var typeIdentity, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            exports.Add(new ManifestContractExportValues(id, version, assemblyIdentity, typeIdentity));
        }

        return true;
    }

    private static bool TryGetImports(
        JsonElement root,
        out List<ManifestContractImportValues> imports,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        imports = new List<ManifestContractImportValues>();
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        if (!root.TryGetProperty("imports", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            failure = ExtensionFailureCode.ManifestSchemaInvalid;
            failureMessage = $"Field 'root.imports' value {DescribeValue(element)} must be a JSON array.";
            return false;
        }

        var index = 0;
        foreach (var declaration in element.EnumerateArray())
        {
            var path = $"imports[{index}]";
            index++;
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                failureMessage = $"Field '{path}' value {DescribeValue(declaration)} must be an import object.";
                return false;
            }

            if (!TryValidateObjectFields(
                    declaration,
                    ManifestSchema.ImportFields,
                    path,
                    out var fields,
                    out failure,
                    out failureMessage))
            {
                return false;
            }

            if (!TryValidateRequiredFields(fields, ManifestSchema.ImportRequiredFields, path, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            if (!TryGetString(declaration, "contractId", path, out var id, out failureMessage) ||
                !TryGetString(declaration, "versionRange", path, out var versionRange, out failureMessage) ||
                !TryGetString(declaration, "assemblyIdentity", path, out var assemblyIdentity, out failureMessage) ||
                !TryGetString(declaration, "typeIdentity", path, out var typeIdentity, out failureMessage) ||
                !TryGetOptionalBool(declaration, "optional", path, out var importOptional, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            imports.Add(new ManifestContractImportValues(id, versionRange, assemblyIdentity, typeIdentity, importOptional));
        }

        return true;
    }

    private static bool TryValidateObjectFields(
        JsonElement element,
        IReadOnlySet<string> allowedFields,
        string path,
        out HashSet<string> fields,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        fields = new HashSet<string>(StringComparer.Ordinal);
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                failure = ExtensionFailureCode.DuplicateManifestField;
                failureMessage =
                    $"Duplicate manifest field {ExtensionDiagnosticText.Value(property.Name)} at '{path}' with value {DescribeValue(property.Value)}.";
                return false;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name))
            {
                failure = ExtensionFailureCode.UnknownManifestField;
                failureMessage = path == "root"
                    ? $"Unknown root field {ExtensionDiagnosticText.Value(property.Name)} has value {DescribeValue(property.Value)}."
                    : $"Unknown field {ExtensionDiagnosticText.Value(property.Name)} at '{path}' has value {DescribeValue(property.Value)}.";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateRequiredFields(
        HashSet<string> fields,
        IReadOnlySet<string> requiredFields,
        string path,
        out string failureMessage)
    {
        foreach (var requiredField in requiredFields)
        {
            if (!fields.Contains(requiredField))
            {
                failureMessage = $"Required field {ExtensionDiagnosticText.Value(requiredField)} at '{path}' is missing.";
                return false;
            }
        }

        failureMessage = string.Empty;
        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        string name,
        string path,
        out string value,
        out string failureMessage)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
        {
            failureMessage = $"Required field '{path}.{name}' is missing.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            failureMessage = $"Field '{path}.{name}' value {DescribeValue(property)} must be a JSON string.";
            return false;
        }

        var text = property.GetString();
        if (text is null)
        {
            failureMessage = $"Field '{path}.{name}' value {DescribeValue(property)} must be a JSON string.";
            return false;
        }

        value = text;
        failureMessage = string.Empty;
        return true;
    }

    private static bool TryGetInt(
        JsonElement element,
        string name,
        string path,
        out int value,
        out string failureMessage)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            failureMessage = $"Required field '{path}.{name}' is missing.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            failureMessage = $"Field '{path}.{name}' value {DescribeValue(property)} must be a 32-bit integer.";
            return false;
        }

        failureMessage = string.Empty;
        return true;
    }

    private static bool TryGetOptionalBool(
        JsonElement element,
        string name,
        string path,
        out bool value,
        out string failureMessage)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
        {
            failureMessage = string.Empty;
            return true;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            failureMessage = $"Field '{path}.{name}' value {DescribeValue(property)} must be a JSON boolean.";
            return false;
        }

        value = property.GetBoolean();
        failureMessage = string.Empty;
        return true;
    }

    private static string DescribeValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => ExtensionDiagnosticText.Value(value.GetString()),
            JsonValueKind.Number => ExtensionDiagnosticText.Value(value.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Object => "<object>",
            JsonValueKind.Array => "<array>",
            _ => $"<{value.ValueKind}>"
        };

    private static ManifestDiscoveryResult Failure(ExtensionFailureCode code, string message) =>
        ManifestParserCore.Failure(ManifestSourceFormat.Json, code, message);
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
        "versionRange",
        "optional"
    };
    internal static readonly IReadOnlySet<string> DependencyRequiredFields = new HashSet<string>(StringComparer.Ordinal)
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
        "typeIdentity",
        "optional"
    };

    internal static readonly IReadOnlySet<string> ImportRequiredFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "contractId",
        "versionRange",
        "assemblyIdentity",
        "typeIdentity"
    };
}
