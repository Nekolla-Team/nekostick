using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Extensions;

internal static class JsonManifestParser
{
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32
    };

    internal static ManifestDiscoveryResult Parse(string root, string manifestPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > MaxManifestBytes)
            {
                return ManifestDiscoveryResult.Failure(ExtensionFailureCode.JsonInvalid, ManifestSourceFormat.Json);
            }

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ManifestDiscoveryResult.Failure(ExtensionFailureCode.JsonInvalid, ManifestSourceFormat.Json);
            }

            var rootFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!rootFields.Add(property.Name))
                {
                    return ManifestDiscoveryResult.Failure(
                        ExtensionFailureCode.DuplicateManifestField,
                        ManifestSourceFormat.Json);
                }
            }

            if (!rootFields.IsSubsetOf(ManifestJsonDto.AllowedFields))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.UnknownManifestField,
                    ManifestSourceFormat.Json);
            }

            if (!TryValidateDependencyFields(
                    document.RootElement,
                    out var dependencyFieldFailure))
            {
                return ManifestDiscoveryResult.Failure(
                    dependencyFieldFailure,
                    ManifestSourceFormat.Json);
            }

            var dto = JsonSerializer.Deserialize<ManifestJsonDto>(bytes, SerializerOptions);
            if (dto is null || dto.SchemaVersion is null || dto.Id is null || dto.Version is null ||
                dto.EntryAssembly is null || dto.EntryType is null ||
                dto.RequiredHostApiVersion is null || dto.Dependencies is null ||
                !HasExactlyRequiredRootFields(rootFields))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.ManifestSchemaInvalid,
                    ManifestSourceFormat.Json);
            }

            if (dto.SchemaVersion != 1)
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.ManifestSchemaInvalid,
                    ManifestSourceFormat.Json);
            }

            if (!ExtensionIdentifierSyntax.IsValid(dto.Id))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.InvalidIdentifier,
                    ManifestSourceFormat.Json);
            }

            if (!SemVersion.TryParse(dto.Version, out var version))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.InvalidVersion,
                    ManifestSourceFormat.Json);
            }

            if (!SemVersionRange.TryParse(dto.RequiredHostApiVersion, out var hostRange) || hostRange is null)
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.InvalidVersionRange,
                    ManifestSourceFormat.Json);
            }

            if (!ManifestNameSyntax.IsValidEntryAssembly(dto.EntryAssembly) ||
                !ManifestNameSyntax.IsValidEntryType(dto.EntryType))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.UnsafePath,
                    ManifestSourceFormat.Json);
            }

            var declaredEntryPath = Path.Combine(
                root,
                dto.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(declaredEntryPath))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.EntryAssemblyMissing,
                    ManifestSourceFormat.Json);
            }

            if (!CanonicalPath.TryCanonicalFileInRoot(
                    root,
                    declaredEntryPath,
                    out var entryAssemblyPath))
            {
                return ManifestDiscoveryResult.Failure(
                    ExtensionFailureCode.UnsafePath,
                    ManifestSourceFormat.Json);
            }

            var dependencies = ImmutableArray.CreateBuilder<ExtensionDependency>(dto.Dependencies.Count);
            var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependencyDto in dto.Dependencies)
            {
                if (dependencyDto is null || !ExtensionIdentifierSyntax.IsValid(dependencyDto.Id))
                {
                    return ManifestDiscoveryResult.Failure(
                        ExtensionFailureCode.InvalidIdentifier,
                        ManifestSourceFormat.Json);
                }

                if (!dependencyIds.Add(dependencyDto.Id!))
                {
                    return ManifestDiscoveryResult.Failure(
                        ExtensionFailureCode.DuplicateExtensionId,
                        ManifestSourceFormat.Json);
                }

                if (!SemVersionRange.TryParse(dependencyDto.VersionRange, out var dependencyRange) ||
                    dependencyRange is null)
                {
                    return ManifestDiscoveryResult.Failure(
                        ExtensionFailureCode.InvalidVersionRange,
                        ManifestSourceFormat.Json);
                }

                dependencies.Add(new ExtensionDependency(dependencyDto.Id!, dependencyRange));
            }

            return ManifestDiscoveryResult.Success(
                ManifestSourceFormat.Json,
                new ExtensionManifest(
                    dto.SchemaVersion.Value,
                    dto.Id,
                    version,
                    dto.EntryAssembly,
                    dto.EntryType,
                    hostRange,
                    dependencies.ToImmutable(),
                    root,
                    entryAssemblyPath));
        }
        catch (JsonException)
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.JsonInvalid, ManifestSourceFormat.Json);
        }
        catch (Exception)
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.LoadFailed, ManifestSourceFormat.Json);
        }
    }

    private static bool HasExactlyRequiredRootFields(HashSet<string> fields) =>
        fields.Count == ManifestJsonDto.AllowedFields.Count &&
        fields.SetEquals(ManifestJsonDto.AllowedFields);

    private static bool TryValidateDependencyFields(
        JsonElement root,
        out ExtensionFailureCode failureCode)
    {
        failureCode = ExtensionFailureCode.None;
        if (!root.TryGetProperty("dependencies", out var dependencies))
        {
            return true;
        }

        if (dependencies.ValueKind != JsonValueKind.Array)
        {
            failureCode = ExtensionFailureCode.ManifestSchemaInvalid;
            return false;
        }

        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (dependency.ValueKind != JsonValueKind.Object)
            {
                failureCode = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in dependency.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                {
                    failureCode = ExtensionFailureCode.DuplicateManifestField;
                    return false;
                }
            }

            if (!fields.IsSubsetOf(DependencyJsonDto.AllowedFields))
            {
                failureCode = ExtensionFailureCode.UnknownManifestField;
                return false;
            }

            if (fields.Count != DependencyJsonDto.AllowedFields.Count ||
                !fields.SetEquals(DependencyJsonDto.AllowedFields))
            {
                failureCode = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }
        }

        return true;
    }

    private sealed class ManifestJsonDto
    {
        internal static readonly ImmutableHashSet<string> AllowedFields =
            ImmutableHashSet.Create(StringComparer.Ordinal,
                "schemaVersion",
                "id",
                "version",
                "entryAssembly",
                "entryType",
                "dependencies",
                "requiredHostApiVersion");

        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("entryAssembly")]
        public string? EntryAssembly { get; set; }

        [JsonPropertyName("entryType")]
        public string? EntryType { get; set; }

        [JsonPropertyName("dependencies")]
        public List<DependencyJsonDto?>? Dependencies { get; set; }

        [JsonPropertyName("requiredHostApiVersion")]
        public string? RequiredHostApiVersion { get; set; }
    }

    private sealed class DependencyJsonDto
    {
        internal static readonly ImmutableHashSet<string> AllowedFields =
            ImmutableHashSet.Create(StringComparer.Ordinal, "id", "versionRange");

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("versionRange")]
        public string? VersionRange { get; set; }
    }
}
