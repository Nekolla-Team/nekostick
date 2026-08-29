namespace Nekolla.Nekostick.Contracts;

/// <summary>Provides lifecycle state, typed contracts, and registration to an extension entrypoint.</summary>
public interface IExtensionStartContext
{
    /// <summary>Gets whether this start is part of replacement reload.</summary>
    bool Reloading { get; }

    /// <summary>Gets the startup-only typed shared-contract registry.</summary>
    IExtensionContractRegistry Contracts { get; }

    /// <summary>Gets the narrow host bridge.</summary>
    IExtensionHostBridge Host { get; }

    /// <summary>Gets the private registration surface.</summary>
    IExtensionRegistration Registration { get; }
}

/// <summary>Exposes only explicitly approved host capabilities to an extension.</summary>
public interface IExtensionHostBridge
{
    /// <summary>Gets the host API version used for compatibility checks.</summary>
    HostApiVersion ApiVersion { get; }

    /// <summary>Gets the legacy read-only versioned extension settings view.</summary>
    IExtensionSettingsReader Configuration { get; }

    /// <summary>Gets the full owned configuration and settings facade introduced in API 1.1.</summary>
    IExtensionConfigurationApi ConfigurationApi { get; }

    /// <summary>Gets trusted full Host business and configuration data access.</summary>
    IExtensionFullConfigurationApi FullConfiguration { get; }

    /// <summary>Gets owned route configuration operations.</summary>
    IExtensionRouteApi Routes { get; }

    /// <summary>Gets owned service configuration and lifecycle operations.</summary>
    IExtensionServiceApi Services { get; }

    /// <summary>Gets read-only published endpoint lease information.</summary>
    IExtensionEndpointApi Endpoints { get; }

    /// <summary>Gets self-scoped lifecycle status and requests.</summary>
    IExtensionLifecycleApi Lifecycle { get; }

    /// <summary>Gets the startup-only typed shared-contract registry.</summary>
    IExtensionContractRegistry Contracts { get; }

    /// <summary>Gets the bounded extension task scheduler.</summary>
    IExtensionTaskScheduler Tasks { get; }

    /// <summary>Gets the ordered best-effort event publisher.</summary>
    IExtensionEventPublisher Events { get; }

    /// <summary>Gets the safe status sink.</summary>
    IExtensionStatusSink Status { get; }

    /// <summary>Gets the safe logger.</summary>
    IExtensionLogger Logger { get; }
}

/// <summary>Exposes additive API 1.3 capabilities without changing the 1.2 bridge contract.</summary>
/// <remarks>
/// An extension opts into this sibling by testing whether its <see cref="IExtensionHostBridge" /> is also an
/// <see cref="IExtensionHostBridge13" /> and by checking <see cref="IExtensionHostBridge.ApiVersion" />.
/// Existing external 1.2 bridge implementers need not implement this interface. The built-in bridge may expose
/// the sibling for an older negotiated version, but returns unsupported behavior rather than a partial capability.
/// </remarks>
public interface IExtensionHostBridge13 : IExtensionHostBridge
{
    /// <summary>Gets global supervised-service runtime telemetry.</summary>
    IExtensionSupervisorApi Supervisor { get; }

    /// <summary>Gets global route observations and action-capable hooks.</summary>
    IExtensionRouteEvents RouteEvents { get; }

    /// <summary>Gets the Host-attributed custom text writer.</summary>
    IExtensionLogWriter LogWriter { get; }

    /// <summary>Gets the extension installation record management and refresh operations.</summary>
    IExtensionManagementApi Management { get; }

    /// <summary>Gets the host-configured data directory for extension-owned persistent files.</summary>
    /// <remarks>
    /// The Host configures this path through its CLI or environment settings and defaults it to a data directory
    /// beside the extensions root. The Host guarantees that the directory exists before the extension starts.
    /// Extensions MUST treat <c>string.Empty</c> as not available and MUST NOT use it as a path. Before accessing
    /// this property, an extension MUST either require host API version 1.3.2 or later in its manifest, or verify
    /// that <see cref="IExtensionHostBridge.ApiVersion" /> is at least 1.3.2. Extensions SHOULD treat this path
    /// as the canonical location for their own persistent files. The default implementation returns an empty
    /// string for older bridge implementers that do not override this member.
    /// </remarks>
    string DataDirectory => string.Empty;
}
