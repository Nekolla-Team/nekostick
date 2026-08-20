using System.Collections.Immutable;
using System.Runtime.Loader;
using System.Reflection;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Identifies one host-approved shared contract assembly outside extension roots.</summary>
public sealed record ExtensionContractCatalogEntry
{
    /// <summary>Creates an immutable catalog entry.</summary>
    /// <param name="assemblyIdentity">The exact <see cref="AssemblyName.FullName" />.</param>
    /// <param name="assemblyPath">The host-owned absolute assembly path.</param>
    public ExtensionContractCatalogEntry(string assemblyIdentity, string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyIdentity) || assemblyIdentity.Length > 2048)
        {
            throw new ArgumentException("A shared assembly identity is required.", nameof(assemblyIdentity));
        }

        if (!Path.IsPathFullyQualified(assemblyPath) || assemblyPath.Length > 4096)
        {
            throw new ArgumentException("A host-owned absolute assembly path is required.", nameof(assemblyPath));
        }

        try
        {
            _ = new AssemblyName(assemblyIdentity);
        }
        catch (Exception exception) when (exception is ArgumentException or FileLoadException)
        {
            throw new ArgumentException("The shared assembly identity is invalid.", nameof(assemblyIdentity), exception);
        }

        AssemblyIdentity = assemblyIdentity;
        AssemblyPath = Path.GetFullPath(assemblyPath);
    }

    /// <summary>Gets the exact assembly identity.</summary>
    public string AssemblyIdentity { get; }

    /// <summary>Gets the host-owned assembly path.</summary>
    public string AssemblyPath { get; }
}

/// <summary>Provides an immutable host-owned catalog of approved shared contract assemblies.</summary>
public sealed class ExtensionContractCatalog
{
    private readonly ImmutableDictionary<string, ExtensionContractCatalogEntry> _entries;
    private readonly Assembly? _trustedAssembly;
    /// <summary>Creates a catalog from host-owned assembly entries.</summary>
    /// <param name="entries">The immutable deployment inventory.</param>
    public ExtensionContractCatalog(IEnumerable<ExtensionContractCatalogEntry>? entries)
        : this(entries, trustedAssembly: null)
    {
    }

    private ExtensionContractCatalog(
        IEnumerable<ExtensionContractCatalogEntry>? entries,
        Assembly? trustedAssembly)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ExtensionContractCatalogEntry>(StringComparer.Ordinal);
        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (entry is null || !builder.TryAdd(entry.AssemblyIdentity, entry))
                {
                    throw new ArgumentException("The shared contract catalog contains a duplicate entry.", nameof(entries));
                }
            }
        }

        _entries = builder.ToImmutable();
        // The default contract assembly is trusted by its loaded identity; single-file deployments have no DLL path.
        _trustedAssembly = trustedAssembly;
    }

    /// <summary>Creates the default catalog containing the stable Nekostick Contracts assembly.</summary>
    public static ExtensionContractCatalog CreateDefault()
    {
        return CreateDefaultForAssembly(typeof(IExtensionEntrypoint).Assembly, assemblyPath: null);
    }

    /// <summary>Creates a default catalog from a loaded contract assembly and optional physical copy.</summary>
    internal static ExtensionContractCatalog CreateDefaultForAssembly(
        Assembly assembly,
        string? assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var identity = assembly.GetName().FullName ??
            throw new InvalidOperationException("The shared contract assembly identity is unavailable.");

        return string.IsNullOrWhiteSpace(assemblyPath)
            ? new ExtensionContractCatalog(null, assembly)
            : new ExtensionContractCatalog(
                [new ExtensionContractCatalogEntry(identity, Path.GetFullPath(assemblyPath))],
                trustedAssembly: null);
    }

    /// <summary>Gets the immutable path-backed assembly inventory.</summary>
    /// <remarks>The default single-file contract is represented by its trusted loaded assembly, not a fabricated path.</remarks>
    public IReadOnlyList<ExtensionContractCatalogEntry> Entries => _entries.Values.ToImmutableArray();

    internal bool TryGetAssembly(string assemblyIdentity, out ExtensionContractCatalogEntry entry)
    {
        if (_entries.TryGetValue(assemblyIdentity, out entry!))
        {
            return true;
        }

        entry = null!;
        return false;
    }

    internal ExtensionFailureCode ValidateDeclaration(
        string extensionRoot,
        string assemblyIdentity,
        string typeIdentity)
    {
        if (!CanonicalPath.TryCanonicalDirectory(extensionRoot, out var canonicalRoot) ||
            !TryGetTrustedAssembly(assemblyIdentity, out var trustedAssembly) &&
            !TryGetAssembly(assemblyIdentity, out _))
        {
            return ExtensionFailureCode.ContractCatalogUnavailable;
        }

        if (trustedAssembly is not null)
        {
            return ValidateAssemblyDeclaration(trustedAssembly, assemblyIdentity, typeIdentity)
                ? ExtensionFailureCode.None
                : ExtensionFailureCode.ContractCatalogUnavailable;
        }

        if (!TryGetAssembly(assemblyIdentity, out var entry) ||
            !CanonicalPath.TryCanonicalExistingFile(entry.AssemblyPath, out var canonicalPath) ||
            CanonicalPath.IsWithin(canonicalRoot, canonicalPath) ||
            !TryValidateAssemblyIdentity(entry, canonicalPath, out var loadedAssembly) ||
            !ValidateAssemblyDeclaration(loadedAssembly, assemblyIdentity, typeIdentity))
        {
            return ExtensionFailureCode.ContractCatalogUnavailable;
        }

        return ExtensionFailureCode.None;
    }

    internal bool TryResolveAssembly(
        AssemblyName requested,
        string extensionRoot,
        out string approvedPath,
        out Assembly? approvedAssembly)
    {
        approvedPath = string.Empty;
        approvedAssembly = null;
        var requestedIdentity = requested.FullName;
        if (requestedIdentity is null ||
            !CanonicalPath.TryCanonicalDirectory(extensionRoot, out var canonicalRoot))
        {
            return false;
        }

        if (TryGetTrustedAssembly(requestedIdentity, out var trustedAssembly))
        {
            approvedAssembly = trustedAssembly;
            return true;
        }

        if (!TryGetAssembly(requestedIdentity, out var entry) ||
            !CanonicalPath.TryCanonicalExistingFile(entry.AssemblyPath, out var canonicalPath) ||
            CanonicalPath.IsWithin(canonicalRoot, canonicalPath) ||
            !TryValidateAssemblyIdentity(entry, canonicalPath, out _))
        {
            return false;
        }

        approvedPath = canonicalPath;
        return true;
    }

    private bool TryGetTrustedAssembly(string assemblyIdentity, out Assembly assembly)
    {
        if (_trustedAssembly is not null &&
            string.Equals(_trustedAssembly.GetName().FullName, assemblyIdentity, StringComparison.Ordinal))
        {
            assembly = _trustedAssembly;
            return true;
        }

        assembly = null!;
        return false;
    }

    private static bool ValidateAssemblyDeclaration(
        Assembly assembly,
        string assemblyIdentity,
        string typeIdentity) =>
        string.Equals(assembly.GetName().FullName, assemblyIdentity, StringComparison.Ordinal) &&
        assembly.GetType(typeIdentity, throwOnError: false, ignoreCase: false) is { } contractType &&
        string.Equals(contractType.Assembly.GetName().FullName, assemblyIdentity, StringComparison.Ordinal);

    private static bool TryValidateAssemblyIdentity(
        ExtensionContractCatalogEntry entry,
        string canonicalPath,
        out Assembly assembly)
    {
        assembly = null!;
        try
        {
            var actual = AssemblyName.GetAssemblyName(canonicalPath);
            if (!string.Equals(actual.FullName, entry.AssemblyIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(canonicalPath);
            return string.Equals(assembly.GetName().FullName, entry.AssemblyIdentity, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
