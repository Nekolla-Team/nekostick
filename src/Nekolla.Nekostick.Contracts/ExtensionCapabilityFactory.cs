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
    IExtensionFullConfigurationApi FullConfiguration)
{
    /// <summary>Creates an extension capability set including optional API 1.3 capabilities.</summary>
    /// <remarks>
    /// The optional log writer must already be bound by the Host to the extension identity supplied to
    /// <see cref="IExtensionCapabilityFactory" /> receives the extension ID and must bind it before constructing the writer; the writer API intentionally accepts no identity argument.
    /// The five-argument primary constructor remains the API 1.2 compatibility path.
    /// </remarks>
    /// <param name="configurationApi">The owner-scoped configuration capability.</param>
    /// <param name="routes">The owner-scoped route capability.</param>
    /// <param name="services">The owner-scoped service capability.</param>
    /// <param name="endpoints">The read-only endpoint capability.</param>
    /// <param name="fullConfiguration">The full configuration capability.</param>
    /// <param name="supervisor">The optional global runtime telemetry capability.</param>
    /// <param name="routeEvents">The optional route observation and hook capability.</param>
    /// <param name="logWriter">The optional Host-attributed text writer.</param>
    public ExtensionCapabilitySet(
        IExtensionConfigurationApi configurationApi,
        IExtensionRouteApi routes,
        IExtensionServiceApi services,
        IExtensionEndpointApi endpoints,
        IExtensionFullConfigurationApi fullConfiguration,
        IExtensionSupervisorApi? supervisor,
        IExtensionRouteEvents? routeEvents,
        IExtensionLogWriter? logWriter)
        : this(configurationApi, routes, services, endpoints, fullConfiguration)
    {
        Supervisor = supervisor;
        RouteEvents = routeEvents;
        LogWriter = logWriter;
    }

    /// <summary>Gets the optional global runtime telemetry capability.</summary>
    public IExtensionSupervisorApi? Supervisor { get; }

    /// <summary>Gets the optional global route observation and hook capability.</summary>
    public IExtensionRouteEvents? RouteEvents { get; }

    /// <summary>Gets the optional Host-attributed custom text writer.</summary>
    public IExtensionLogWriter? LogWriter { get; }
}

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
