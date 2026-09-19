namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the resolution state of one declared extension dependency.</summary>
public enum ExtensionDependencyState
{
    /// <summary>The dependency is present and its installed version satisfies the declared range.</summary>
    Satisfied,

    /// <summary>The dependency extension is not present in the current extension set.</summary>
    NotInstalled,

    /// <summary>The dependency extension is present but its installed version does not satisfy the declared range.</summary>
    VersionMismatch,

    /// <summary>The queried extension identifier is not declared as a dependency of the calling extension.</summary>
    NotDeclared,

    /// <summary>The dependency API is not available for the negotiated host API version.</summary>
    Unavailable
}

/// <summary>Describes one declared dependency of the calling extension and resolves its contracts.</summary>
/// <remarks>
/// The context is a snapshot taken when the calling extension started. A reloaded or updated dependency
/// is reflected only after the calling extension itself starts again, matching the shared-contract
/// exchange semantics.
/// </remarks>
public interface IExtensionDependencyContext
{
    /// <summary>Gets the queried dependency extension identifier.</summary>
    string ExtensionId { get; }

    /// <summary>Gets the resolution state of the declared dependency.</summary>
    ExtensionDependencyState State { get; }

    /// <summary>Gets whether the dependency was declared optional in the manifest.</summary>
    bool IsOptional { get; }
    /// <summary>Gets the declared dependency version range text.</summary>
    string VersionRange { get; }

    /// <summary>Gets the installed dependency version when the dependency extension is present.</summary>
    string? InstalledVersion { get; }

    /// <summary>Imports one strongly typed implementation from this dependency when it is satisfied.</summary>
    /// <typeparam name="TContract">The approved shared contract type.</typeparam>
    /// <param name="contractId">The declared stable contract ID.</param>
    /// <param name="contract">The resolved implementation when available.</param>
    /// <returns>
    /// <see langword="true" /> when the dependency is <see cref="ExtensionDependencyState.Satisfied" /> and a
    /// compatible provider exported the contract during startup; otherwise <see langword="false" />. Like
    /// <see cref="IExtensionContractRegistry.TryImport{TContract}" />, the exchange is startup-only.
    /// </returns>
    bool TryImport<TContract>(string contractId, out TContract? contract)
        where TContract : class;
}

/// <summary>Exposes dependency resolution information for the calling extension.</summary>
/// <remarks>Introduced in API 1.4. Extensions MUST verify the negotiated host API version before use.</remarks>
public interface IExtensionDependencyApi
{
    /// <summary>Resolves the dependency context for one declared dependency.</summary>
    /// <param name="extensionId">The dependency extension identifier.</param>
    /// <returns>
    /// The dependency context. When <paramref name="extensionId" /> is not declared as a dependency of the
    /// calling extension, the context reports <see cref="ExtensionDependencyState.NotDeclared" />.
    /// </returns>
    IExtensionDependencyContext GetDependencyContext(string extensionId);
}
