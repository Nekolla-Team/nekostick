using System.Reflection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionContractCatalogTests
{
    [Fact]
    public void TrustedLoadedAssemblyResolvesWhenPhysicalPathIsUnavailable()
    {
        var sourcePath = GetKnownOutputAssemblyPath(typeof(IExtensionEntrypoint).Assembly);
        var loadedAssembly = Assembly.Load(File.ReadAllBytes(sourcePath));
        Assert.Empty(loadedAssembly.Location);
        var catalog = ExtensionContractCatalog.CreateDefaultForAssembly(
            loadedAssembly,
            assemblyPath: null);
        var root = Path.Combine(
            Path.GetTempPath(),
            "nekostick-contract-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var identity = loadedAssembly.GetName().FullName!;
            Assert.Empty(catalog.Entries);
            Assert.Equal(
                ExtensionFailureCode.None,
                catalog.ValidateDeclaration(
                    root,
                    identity,
                    typeof(IExtensionEntrypoint).FullName!));

            Assert.True(catalog.TryResolveAssembly(
                new AssemblyName(identity),
                root,
                out var approvedPath,
                out var approvedAssembly));
            Assert.Empty(approvedPath);
            Assert.Same(loadedAssembly, approvedAssembly);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrustedLoadedAssemblyRejectsAnUnapprovedIdentity()
    {
        var sourcePath = GetKnownOutputAssemblyPath(typeof(IExtensionEntrypoint).Assembly);
        var loadedAssembly = Assembly.Load(File.ReadAllBytes(sourcePath));
        var catalog = ExtensionContractCatalog.CreateDefaultForAssembly(
            loadedAssembly,
            assemblyPath: null);
        var root = Path.Combine(
            Path.GetTempPath(),
            "nekostick-contract-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Assert.False(catalog.TryResolveAssembly(
                new AssemblyName("Unapproved.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"),
                root,
                out _,
                out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string GetKnownOutputAssemblyPath(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("The contract assembly name is unavailable.");
        }

        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The contract assembly is not present in the test output.");
        }

        var actual = AssemblyName.GetAssemblyName(path);
        if (!string.Equals(actual.FullName, assembly.GetName().FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The contract assembly identity is not the expected output assembly.");
        }

        return path;
    }
}
