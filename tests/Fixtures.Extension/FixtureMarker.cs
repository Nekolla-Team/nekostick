namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

internal static class FixtureMarker
{
    internal const string ContractAssemblyBoundary = "Nekolla.Nekostick.Contracts";
}

/// <summary>Identifies the explicit-load extension fixture assembly.</summary>
public static class FixtureAssemblyMarker
{
    /// <summary>Gets the stable fixture assembly name.</summary>
    public const string AssemblyName = "Fixtures.Extension";

    /// <summary>Gets the only project reference permitted for this fixture.</summary>
    public const string ContractAssemblyName = "Nekolla.Nekostick.Contracts";

    /// <summary>Gets the status of extension ABI binding for this fixture.</summary>
    public const string AbiBindingStatus = "No extension ABI is currently bound.";
}
