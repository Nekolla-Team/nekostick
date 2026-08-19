using System.Collections.Immutable;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Identifies the manifest source format examined by the core.</summary>
public enum ManifestSourceFormat
{
    /// <summary>The strict JSON manifest format.</summary>
    Json,

    /// <summary>The safe scalar/map/list YAML manifest format.</summary>
    Yaml
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

    /// <summary>A YAML manifest was selected but is invalid.</summary>
    YamlInvalid,
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
    /// <summary>The requested shared contract catalog entry is unavailable.</summary>
    ContractCatalogUnavailable,

    /// <summary>A manifest contains duplicate export or import declarations.</summary>
    DuplicateContractDeclaration,

    /// <summary>An imported contract has no compatible provider.</summary>
    MissingContractProvider,

    /// <summary>A provider contract version does not satisfy the import range.</summary>
    ContractVersionIncompatible,

    /// <summary>A shared contract assembly or type identity is incompatible.</summary>
    ContractIdentityMismatch,

    /// <summary>The requested contracts assembly identity was not approved.</summary>
    ContractsIdentityMismatch,

    /// <summary>The entry type could not be found.</summary>
    EntryTypeMissing,

    /// <summary>The entry type does not implement the public Contracts lifecycle ABI.</summary>
    EntryTypeNotCompatible,

    /// <summary>The collectible load operation failed.</summary>
    LoadFailed,

    /// <summary>The unload operation was already completed.</summary>
    AlreadyUnloaded,

    /// <summary>The unload operation is currently in progress.</summary>
    UnloadInProgress,
    /// <summary>The extension entry constructor failed safely.</summary>
    EntryConstructorFailed,

    /// <summary>The extension lifecycle callback failed safely.</summary>
    LifecycleFailed,

    /// <summary>The extension handler registry conflicts with an existing registration.</summary>
    HandlerConflict,

    /// <summary>The global extension fallback is already registered.</summary>
    FallbackConflict,

    /// <summary>The extension runtime operation could not complete.</summary>
    RuntimeUnavailable,

    /// <summary>The requested extension handler failed safely.</summary>
    HandlerFailed,

    /// <summary>The event or task callback failed safely.</summary>
    CallbackFailed,

    /// <summary>The extension reached its rolling failure threshold.</summary>
    FailureThresholdReached,

    /// <summary>The requested extension is not loaded.</summary>
    ExtensionNotLoaded,

    /// <summary>The requested handler is not available.</summary>
    HandlerUnavailable,

    /// <summary>The operation was cancelled before completion.</summary>
    Cancelled,

    /// <summary>The replacement could not be committed and the previous instance was preserved.</summary>
    ReplacementPreserved,

    /// <summary>The extension handler drain exceeded its bounded timeout.</summary>
    DrainTimeout,

    /// <summary>The previous extension stop failed safely.</summary>
    StopFailed,

    /// <summary>The extension was already stopped.</summary>
    AlreadyStopped,

    /// <summary>The extension task limit was reached.</summary>
    TaskLimitReached,

    /// <summary>The event queue is full and dropped the newest event.</summary>
    EventQueueFull,

    /// <summary>The collectible context could not be released after bounded verification.</summary>
    UnloadLeak,

    /// <summary>The collectible context could not be released after bounded verification.</summary>
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

/// <summary>Declares one shared contract exported by an extension.</summary>
public sealed record ExtensionContractExport
{
    /// <summary>Creates an export declaration.</summary>
    public ExtensionContractExport(
        string contractId,
        SemVersion version,
        string assemblyIdentity,
        string typeIdentity)
    {
        ValidateIdentity(contractId, assemblyIdentity, typeIdentity);
        ContractId = contractId;
        Version = version;
        AssemblyIdentity = assemblyIdentity;
        TypeIdentity = typeIdentity;
    }

    /// <summary>Gets the stable contract ID.</summary>
    public string ContractId { get; }

    /// <summary>Gets the exported semantic version.</summary>
    public SemVersion Version { get; }

    /// <summary>Gets the exact shared assembly identity.</summary>
    public string AssemblyIdentity { get; }

    /// <summary>Gets the exact shared contract type identity.</summary>
    public string TypeIdentity { get; }

    internal static void ValidateIdentity(string contractId, string assemblyIdentity, string typeIdentity)
    {
        if (!ExtensionIdentifierSyntax.IsValid(contractId))
        {
            throw new ArgumentException("A safe contract identifier is required.", nameof(contractId));
        }

        if (string.IsNullOrWhiteSpace(assemblyIdentity) || assemblyIdentity.Length > 1024)
        {
            throw new ArgumentException("A shared assembly identity is required.", nameof(assemblyIdentity));
        }

        if (string.IsNullOrWhiteSpace(typeIdentity) || typeIdentity.Length > 1024)
        {
            throw new ArgumentException("A shared type identity is required.", nameof(typeIdentity));
        }
    }
}

/// <summary>Declares one shared contract imported by an extension.</summary>
public sealed record ExtensionContractImport
{
    /// <summary>Creates an import declaration.</summary>
    public ExtensionContractImport(
        string contractId,
        SemVersionRange versionRange,
        string assemblyIdentity,
        string typeIdentity)
    {
        ExtensionContractExport.ValidateIdentity(contractId, assemblyIdentity, typeIdentity);
        ContractId = contractId;
        VersionRange = versionRange ?? throw new ArgumentNullException(nameof(versionRange));
        AssemblyIdentity = assemblyIdentity;
        TypeIdentity = typeIdentity;
    }

    /// <summary>Gets the stable contract ID.</summary>
    public string ContractId { get; }

    /// <summary>Gets the accepted semantic version range.</summary>
    public SemVersionRange VersionRange { get; }

    /// <summary>Gets the exact shared assembly identity.</summary>
    public string AssemblyIdentity { get; }

    /// <summary>Gets the exact shared contract type identity.</summary>
    public string TypeIdentity { get; }
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
        ImmutableArray<ExtensionContractExport> exports,
        ImmutableArray<ExtensionContractImport> imports,
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
        Exports = exports.IsDefault ? ImmutableArray<ExtensionContractExport>.Empty : exports;
        Imports = imports.IsDefault ? ImmutableArray<ExtensionContractImport>.Empty : imports;
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
    /// <summary>Gets the immutable shared contract exports.</summary>
    public ImmutableArray<ExtensionContractExport> Exports { get; }

    /// <summary>Gets the immutable shared contract imports.</summary>
    public ImmutableArray<ExtensionContractImport> Imports { get; }

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
