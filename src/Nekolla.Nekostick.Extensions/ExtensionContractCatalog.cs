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

    /// <summary>Creates a catalog from host-owned assembly entries.</summary>
    /// <param name="entries">The immutable deployment inventory.</param>
    public ExtensionContractCatalog(IEnumerable<ExtensionContractCatalogEntry>? entries)
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
    }

    /// <summary>Creates the default catalog containing the stable Nekostick Contracts assembly.</summary>
    public static ExtensionContractCatalog CreateDefault()
    {
        var assembly = typeof(IExtensionEntrypoint).Assembly;
        return string.IsNullOrWhiteSpace(assembly.Location)
            ? new ExtensionContractCatalog(null)
            : new ExtensionContractCatalog(
                [new ExtensionContractCatalogEntry(
                    assembly.GetName().FullName!,
                    Path.GetFullPath(assembly.Location))]);
    }

    /// <summary>Gets the immutable assembly inventory.</summary>
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
        if (!TryGetAssembly(assemblyIdentity, out var entry) ||
            !CanonicalPath.TryCanonicalDirectory(extensionRoot, out var canonicalRoot) ||
            !CanonicalPath.TryCanonicalExistingFile(entry.AssemblyPath, out var canonicalPath) ||
            CanonicalPath.IsWithin(canonicalRoot, canonicalPath) ||
            !TryValidateAssemblyIdentity(entry, canonicalPath, out var loadedAssembly) ||
            loadedAssembly.GetType(typeIdentity, throwOnError: false, ignoreCase: false) is not { } contractType ||
            !string.Equals(contractType.Assembly.GetName().FullName, assemblyIdentity, StringComparison.Ordinal))
        {
            return ExtensionFailureCode.ContractCatalogUnavailable;
        }

        return ExtensionFailureCode.None;
    }

    internal bool TryResolveAssembly(
        AssemblyName requested,
        string extensionRoot,
        out string approvedPath)
    {
        approvedPath = string.Empty;
        var requestedIdentity = requested.FullName;
        if (requestedIdentity is null || !TryGetAssembly(requestedIdentity, out var entry) ||
            !CanonicalPath.TryCanonicalDirectory(extensionRoot, out var canonicalRoot) ||
            !CanonicalPath.TryCanonicalExistingFile(entry.AssemblyPath, out var canonicalPath) ||
            CanonicalPath.IsWithin(canonicalRoot, canonicalPath) ||
            !TryValidateAssemblyIdentity(entry, canonicalPath, out _))
        {
            return false;
        }

        approvedPath = canonicalPath;
        return true;
    }

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
