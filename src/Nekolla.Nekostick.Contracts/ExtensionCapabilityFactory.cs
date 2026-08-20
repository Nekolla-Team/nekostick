using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Provides host-created extension capability facades.</summary>
/// <remarks>The factory crosses the extension boundary only with Contracts DTOs and facades.</remarks>
public interface IExtensionCapabilityFactory
{
    /// <summary>Creates one capability set for an extension.</summary>
    /// <param name="extensionId">The host-validated extension identifier.</param>
    /// <param name="handlerIsOwned">Checks a stable handler identifier owned by the extension.</param>
    /// <returns>The immutable set of approved facades.</returns>
    ExtensionCapabilitySet Create(
        string extensionId,
        Func<string, bool> handlerIsOwned);
}

/// <summary>Contains the approved capability facades supplied to a bridge.</summary>
public sealed record ExtensionCapabilitySet(
    IExtensionConfigurationApi ConfigurationApi,
    IExtensionRouteApi Routes,
    IExtensionServiceApi Services,
    IExtensionEndpointApi Endpoints,
    IExtensionFullConfigurationApi FullConfiguration);

/// <summary>Provides the persistence-backed owner-scoped configuration seam used by Host facades.</summary>
public interface IExtensionOwnedConfigurationApi
{
    /// <summary>Reads only records owned by the supplied extension.</summary>
    ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadOwnedAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Applies an owner-scoped atomic change set.</summary>
    ValueTask<ConfigurationWriteResult> ApplyOwnedAsync(
        string extensionId,
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        Func<string, bool>? handlerIsOwned = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads only the supplied extension's settings.</summary>
    ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadOwnedSettingsAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Writes only the supplied extension's settings.</summary>
    ValueTask<ConfigurationWriteResult> WriteOwnedSettingsAsync(
        string extensionId,
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default);
}
