using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Identifies the manifest source format examined by the core.</summary>
public enum ManifestSourceFormat
{
    /// <summary>The strict JSON manifest format.</summary>
    Json,

    /// <summary>YAML was selected, but its dependency-backed adapter is deferred.</summary>
    YamlDeferred
}

/// <summary>Provides stable, non-sensitive failure categories for extension core operations.</summary>
public enum ExtensionFailureCode
{
    /// <summary>No failure occurred.</summary>
    None,

    /// <summary>The caller supplied an invalid argument.</summary>
    InvalidArgument,

    /// <summary>No supported manifest was found.</summary>
    ManifestMissing,

    /// <summary>More than one manifest file was found.</summary>
    DuplicateManifest,

    /// <summary>A YAML manifest was selected without a parser adapter.</summary>
    YamlParserDeferred,

    /// <summary>The manifest was not valid strict JSON.</summary>
    JsonInvalid,

    /// <summary>The manifest contains an unknown field.</summary>
    UnknownManifestField,

    /// <summary>The manifest contains a duplicate field.</summary>
    DuplicateManifestField,

    /// <summary>The manifest schema version is unsupported or malformed.</summary>
    ManifestSchemaInvalid,

    /// <summary>The manifest identifier is invalid.</summary>
    InvalidIdentifier,

    /// <summary>A semantic version is invalid.</summary>
    InvalidVersion,

    /// <summary>A semantic version range is invalid.</summary>
    InvalidVersionRange,

    /// <summary>An entry assembly or type name is unsafe.</summary>
    UnsafePath,

    /// <summary>The declared entry assembly does not exist inside the extension root.</summary>
    EntryAssemblyMissing,

    /// <summary>The required host API range is not satisfied.</summary>
    HostApiIncompatible,

    /// <summary>Two discovered manifests use the same stable identifier.</summary>
    DuplicateExtensionId,

    /// <summary>A declared dependency is not present.</summary>
    MissingDependency,

    /// <summary>A dependency version range is not satisfied.</summary>
    DependencyVersionIncompatible,

    /// <summary>The dependency graph contains a cycle.</summary>
    DependencyCycle,

    /// <summary>The requested contracts assembly identity was not approved.</summary>
    ContractsIdentityMismatch,

    /// <summary>The entry type could not be found.</summary>
    EntryTypeMissing,

    /// <summary>The entry type does not implement the current internal marker.</summary>
    EntryTypeNotCompatible,

    /// <summary>The collectible load operation failed.</summary>
    LoadFailed,

    /// <summary>The unload operation was already completed.</summary>
    AlreadyUnloaded,

    /// <summary>The unload operation is currently in progress.</summary>
    UnloadInProgress,

    /// <summary>The collectible context was not confirmed released after three GC cycles.</summary>
    UnloadNotConfirmed
}

/// <summary>Describes one manifest dependency.</summary>
public sealed record ExtensionDependency
{
    /// <summary>Creates a dependency declaration.</summary>
    /// <param name="id">The required extension identifier.</param>
    /// <param name="versionRange">The required version range.</param>
    public ExtensionDependency(string id, SemVersionRange versionRange)
    {
        if (!ExtensionIdentifierSyntax.IsValid(id))
        {
            throw new ArgumentException("A safe dependency identifier is required.", nameof(id));
        }

        Id = id;
        VersionRange = versionRange ?? throw new ArgumentNullException(nameof(versionRange));
    }

    /// <summary>Gets the required extension identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the required version range.</summary>
    public SemVersionRange VersionRange { get; }
}

/// <summary>Contains the validated immutable extension manifest.</summary>
public sealed class ExtensionManifest
{
    internal ExtensionManifest(
        int schemaVersion,
        string id,
        SemVersion version,
        string entryAssembly,
        string entryType,
        SemVersionRange requiredHostApiVersion,
        ImmutableArray<ExtensionDependency> dependencies,
        string extensionDirectory,
        string entryAssemblyPath)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        Version = version;
        EntryAssembly = entryAssembly;
        EntryType = entryType;
        RequiredHostApiVersion = requiredHostApiVersion;
        Dependencies = dependencies.IsDefault ? ImmutableArray<ExtensionDependency>.Empty : dependencies;
        ExtensionDirectory = extensionDirectory;
        EntryAssemblyPath = entryAssemblyPath;
    }

    /// <summary>Gets the supported manifest schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable extension identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the extension semantic version.</summary>
    public SemVersion Version { get; }

    /// <summary>Gets the relative entry assembly path.</summary>
    public string EntryAssembly { get; }

    /// <summary>Gets the fully qualified entry type name.</summary>
    public string EntryType { get; }

    /// <summary>Gets the required host API version range.</summary>
    public SemVersionRange RequiredHostApiVersion { get; }

    /// <summary>Gets the immutable dependency declarations.</summary>
    public ImmutableArray<ExtensionDependency> Dependencies { get; }

    internal string ExtensionDirectory { get; }

    internal string EntryAssemblyPath { get; }
}

/// <summary>Represents the result of explicit manifest discovery for one directory.</summary>
public sealed class ManifestDiscoveryResult
{
    private ManifestDiscoveryResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ManifestSourceFormat? sourceFormat,
        ExtensionManifest? manifest)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        SourceFormat = sourceFormat;
        Manifest = manifest;
    }

    /// <summary>Gets whether discovery succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe failure category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the selected source format when one was selected.</summary>
    public ManifestSourceFormat? SourceFormat { get; }

    /// <summary>Gets the immutable manifest on success.</summary>
    public ExtensionManifest? Manifest { get; }

    internal static ManifestDiscoveryResult Success(ManifestSourceFormat format, ExtensionManifest manifest) =>
        new(true, ExtensionFailureCode.None, format, manifest);

    internal static ManifestDiscoveryResult Failure(ExtensionFailureCode code, ManifestSourceFormat? format = null) =>
        new(false, code, format, null);
}

/// <summary>Discovers exactly one manifest at an explicitly supplied extension directory.</summary>
public static class ExtensionManifestDiscovery
{
    /// <summary>Reads and validates one explicit extension directory.</summary>
    /// <param name="extensionDirectory">The exact directory supplied by the caller.</param>
    /// <returns>A safe result without filesystem paths in failure data.</returns>
    public static ManifestDiscoveryResult Discover(string? extensionDirectory)
    {
        if (!CanonicalPath.TryCanonicalDirectory(extensionDirectory, out var root))
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        var manifestFiles = new List<(string Name, ManifestSourceFormat Format)>();
        AddExistingManifest(root, "manifest.json", ManifestSourceFormat.Json, manifestFiles);
        AddExistingManifest(root, "manifest.yaml", ManifestSourceFormat.YamlDeferred, manifestFiles);
        AddExistingManifest(root, "manifest.yml", ManifestSourceFormat.YamlDeferred, manifestFiles);

        if (manifestFiles.Count == 0)
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.ManifestMissing);
        }

        if (manifestFiles.Count != 1)
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.DuplicateManifest);
        }

        var selected = manifestFiles[0];
        if (selected.Format == ManifestSourceFormat.YamlDeferred)
        {
            return ManifestDiscoveryResult.Failure(
                ExtensionFailureCode.YamlParserDeferred,
                ManifestSourceFormat.YamlDeferred);
        }

        var manifestPath = Path.Combine(root, selected.Name);
        if (!CanonicalPath.TryCanonicalFileInRoot(root, manifestPath, out var canonicalManifestPath))
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.UnsafePath, selected.Format);
        }

        return JsonManifestParser.Parse(root, canonicalManifestPath);
    }

    private static void AddExistingManifest(
        string root,
        string name,
        ManifestSourceFormat format,
        List<(string Name, ManifestSourceFormat Format)> manifestFiles)
    {
        var path = Path.Combine(root, name);
        if (File.Exists(path))
        {
            manifestFiles.Add((name, format));
        }
    }
}

/// <summary>Represents the deterministic result of dependency graph validation.</summary>
public sealed class ExtensionGraphResult
{
    private ExtensionGraphResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ImmutableArray<ExtensionManifest> orderedManifests)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        OrderedManifests = orderedManifests;
    }

    /// <summary>Gets whether graph validation succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe graph failure category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the deterministic topological load order.</summary>
    public ImmutableArray<ExtensionManifest> OrderedManifests { get; }

    internal static ExtensionGraphResult Success(ImmutableArray<ExtensionManifest> manifests) =>
        new(true, ExtensionFailureCode.None, manifests);

    internal static ExtensionGraphResult Failure(ExtensionFailureCode code) =>
        new(false, code, ImmutableArray<ExtensionManifest>.Empty);
}

/// <summary>Validates dependencies and produces a deterministic topological order.</summary>
public static class ExtensionManifestGraph
{
    /// <summary>Validates an explicitly supplied manifest set.</summary>
    /// <param name="manifests">The manifests already discovered by the caller.</param>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    /// <returns>A graph result whose layers are ordinal ID sorted.</returns>
    public static ExtensionGraphResult ValidateAndOrder(
        IEnumerable<ExtensionManifest>? manifests,
        SemVersion hostApiVersion)
    {
        if (manifests is null)
        {
            return ExtensionGraphResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        ImmutableArray<ExtensionManifest> items;
        try
        {
            items = manifests.ToImmutableArray();
        }
        catch (Exception)
        {
            return ExtensionGraphResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        var byId = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        foreach (var manifest in items)
        {
            if (manifest is null || !ExtensionIdentifierSyntax.IsValid(manifest.Id))
            {
                return ExtensionGraphResult.Failure(ExtensionFailureCode.InvalidIdentifier);
            }

            if (!byId.TryAdd(manifest.Id, manifest))
            {
                return ExtensionGraphResult.Failure(ExtensionFailureCode.DuplicateExtensionId);
            }

            if (!manifest.RequiredHostApiVersion.IsSatisfiedBy(hostApiVersion))
            {
                return ExtensionGraphResult.Failure(ExtensionFailureCode.HostApiIncompatible);
            }
        }

        var indegrees = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var manifest in items)
        {
            var dependencies = manifest.Dependencies;
            var uniqueDependencies = new HashSet<string>(StringComparer.Ordinal);
            indegrees[manifest.Id] = dependencies.Length;
            foreach (var dependency in dependencies)
            {
                if (!uniqueDependencies.Add(dependency.Id))
                {
                    return ExtensionGraphResult.Failure(ExtensionFailureCode.DuplicateExtensionId);
                }

                if (!byId.TryGetValue(dependency.Id, out var dependencyManifest))
                {
                    return ExtensionGraphResult.Failure(ExtensionFailureCode.MissingDependency);
                }

                if (!dependency.VersionRange.IsSatisfiedBy(dependencyManifest.Version))
                {
                    return ExtensionGraphResult.Failure(ExtensionFailureCode.DependencyVersionIncompatible);
                }

                if (!dependents.TryGetValue(dependency.Id, out var dependentList))
                {
                    dependentList = new List<string>();
                    dependents.Add(dependency.Id, dependentList);
                }

                dependentList.Add(manifest.Id);
            }
        }

        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in indegrees)
        {
            if (item.Value == 0)
            {
                ready.Add(item.Key);
            }
        }

        var ordered = ImmutableArray.CreateBuilder<ExtensionManifest>(items.Length);
        while (ready.Count > 0)
        {
            var layer = ready.ToArray();
            ready.Clear();
            foreach (var id in layer)
            {
                ordered.Add(byId[id]);
            }

            foreach (var id in layer)
            {
                if (!dependents.TryGetValue(id, out var dependentList))
                {
                    continue;
                }

                foreach (var dependentId in dependentList)
                {
                    indegrees[dependentId]--;
                    if (indegrees[dependentId] == 0)
                    {
                        ready.Add(dependentId);
                    }
                }
            }
        }

        return ordered.Count == items.Length
            ? ExtensionGraphResult.Success(ordered.ToImmutable())
            : ExtensionGraphResult.Failure(ExtensionFailureCode.DependencyCycle);
    }
}

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

internal static class ExtensionIdentifierSyntax
{
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        var segmentLength = 0;
        foreach (var character in value)
        {
            var isLowerAlphaNumeric = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (isLowerAlphaNumeric)
            {
                segmentLength++;
                continue;
            }

            if (character is '.' or '-')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                continue;
            }

            return false;
        }

        return segmentLength > 0;
    }
}

internal static class ManifestNameSyntax
{
    internal static bool IsValidEntryAssembly(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 ||
            !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            value[0] is '/' or '\\' ||
            value.Contains('\\') ||
            value.Contains(':'))
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment is "" or "." or ".." || segment.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsValidEntryType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512)
        {
            return false;
        }

        var segmentLength = 0;
        var segmentStart = true;
        foreach (var character in value)
        {
            var isIdentifierCharacter = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (isIdentifierCharacter || character == '_')
            {
                if (segmentStart && character is >= '0' and <= '9')
                {
                    return false;
                }

                segmentLength++;
                segmentStart = false;
                continue;
            }

            if (character is '.' or '+')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                segmentStart = true;
                continue;
            }

            return false;
        }

        return segmentLength > 0;
    }
}

internal static class CanonicalPath
{
    internal static bool TryCanonicalDirectory(string? input, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Contains('\0'))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(input);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (pathRoot is null)
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(pathRoot, fullPath);
            if (relativePath == ".")
            {
                directory = Normalize(pathRoot);
                return Directory.Exists(directory);
            }

            var current = Normalize(pathRoot);
            var segments = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                var info = new DirectoryInfo(current);
                if (!info.Exists)
                {
                    directory = string.Empty;
                    return false;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                current = Normalize(target?.FullName ?? info.FullName);
            }

            directory = current;
            return Directory.Exists(directory);
        }
        catch (Exception)
        {
            directory = string.Empty;
            return false;
        }
    }

    internal static bool TryCanonicalFileInRoot(string root, string candidate, out string file)
    {
        file = string.Empty;
        try
        {
            var relativeCandidate = Path.GetRelativePath(root, candidate);
            if (relativeCandidate is "." || relativeCandidate.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativeCandidate))
            {
                return false;
            }

            var current = root;
            var segments = relativeCandidate.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                FileSystemInfo item = index == segments.Length - 1
                    ? new FileInfo(current)
                    : new DirectoryInfo(current);
                if (!item.Exists)
                {
                    return false;
                }

                var target = item.ResolveLinkTarget(returnFinalTarget: true);
                current = Normalize(target?.FullName ?? item.FullName);
            }

            if (!File.Exists(current) || !IsWithin(root, current))
            {
                return false;
            }

            file = current;
            return true;
        }
        catch (Exception)
        {
            file = string.Empty;
            return false;
        }
    }

    internal static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var separator = normalizedRoot.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? string.Empty
            : Path.DirectorySeparatorChar.ToString();
        return normalizedCandidate.Equals(normalizedRoot, comparison) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + separator,
                comparison);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }
}
