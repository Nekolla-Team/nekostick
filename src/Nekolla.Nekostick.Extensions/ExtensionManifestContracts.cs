using System.Collections.Immutable;

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
