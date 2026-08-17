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

            if (!rootFields.SetEquals(ManifestSchema.AllowedFields))
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
                    hostApiVersion));
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
        "requiredHostApiVersion"
    };

    internal static readonly IReadOnlySet<string> DependencyFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "id",
        "versionRange"
    };
}
