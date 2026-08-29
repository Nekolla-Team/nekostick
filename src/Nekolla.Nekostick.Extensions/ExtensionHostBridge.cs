using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal sealed class ExtensionHostBridge : IExtensionHostBridge13
{
    internal ExtensionHostBridge(
        HostApiVersion apiVersion,
        ExtensionSettingsConfiguration? settings,
        ExtensionTaskTracker tasks,
        ExtensionEventQueue events,
        ExtensionContractRegistry contracts,
        ExtensionCapabilitySet capabilities,
        IExtensionLifecycleApi lifecycle,
        Action<ExtensionStatus> reportStatus,
        Action<ExtensionLogLevel, string> reportLog,
        string? dataDirectory = null)
    {
        ApiVersion = apiVersion;
        DataDirectory = dataDirectory ?? string.Empty;
        Configuration = new ExtensionSettingsReader(settings);
        var api11Supported = ExtensionApiCapabilityGate.IsApi11Supported(apiVersion);
        var api12Supported = ExtensionApiCapabilityGate.IsApi12Supported(apiVersion);
        var unsupported = UnsupportedExtensionCapabilities.Create(apiVersion);
        ConfigurationApi = api11Supported ? capabilities.ConfigurationApi : unsupported.ConfigurationApi;
        FullConfiguration = api12Supported ? capabilities.FullConfiguration : unsupported.FullConfiguration;
        Routes = api11Supported ? capabilities.Routes : unsupported.Routes;
        Services = api11Supported ? capabilities.Services : unsupported.Services;
        Endpoints = api11Supported ? capabilities.Endpoints : unsupported.Endpoints;
        Lifecycle = api11Supported
            ? lifecycle
            : UnsupportedExtensionCapabilities.CreateLifecycle();
        Contracts = contracts;
        Tasks = tasks;
        Events = events;
        Status = new ExtensionStatusSink(reportStatus);
        Logger = new ExtensionLogger(reportLog);

        var api13Supported = ExtensionAbi.IsApi13Supported(apiVersion);
        Supervisor = api13Supported
            ? capabilities.Supervisor ?? UnsupportedExtensionCapabilities.CreateSupervisor()
            : UnsupportedExtensionCapabilities.CreateSupervisor();
        RouteEvents = api13Supported
            ? capabilities.RouteEvents ?? UnsupportedExtensionCapabilities.CreateRouteEvents()
            : UnsupportedExtensionCapabilities.CreateRouteEvents();
        LogWriter = api13Supported
            ? capabilities.LogWriter ?? UnsupportedExtensionCapabilities.CreateLogWriter()
            : UnsupportedExtensionCapabilities.CreateLogWriter();
        Management = api13Supported
            ? capabilities.ExtensionManagement ?? UnsupportedExtensionCapabilities.CreateManagement(apiVersion)
            : UnsupportedExtensionCapabilities.CreateManagement(apiVersion);
    }

    public HostApiVersion ApiVersion { get; }

    public string DataDirectory { get; }

    public IExtensionSettingsReader Configuration { get; }
    public IExtensionConfigurationApi ConfigurationApi { get; }
    public IExtensionFullConfigurationApi FullConfiguration { get; }
    public IExtensionRouteApi Routes { get; }
    public IExtensionServiceApi Services { get; }
    public IExtensionEndpointApi Endpoints { get; }
    public IExtensionLifecycleApi Lifecycle { get; }

    public IExtensionTaskScheduler Tasks { get; }

    public IExtensionEventPublisher Events { get; }
    public IExtensionContractRegistry Contracts { get; }

    public IExtensionStatusSink Status { get; }

    public IExtensionLogger Logger { get; }
    public IExtensionSupervisorApi Supervisor { get; }

    public IExtensionRouteEvents RouteEvents { get; }

    public IExtensionLogWriter LogWriter { get; }
    public IExtensionManagementApi Management { get; }
}

internal sealed class ExtensionStartContext : IExtensionStartContext
{
    internal ExtensionStartContext(
        bool reloading,
        IExtensionHostBridge host,
        ExtensionContractRegistry contracts,
        ExtensionHandlerRegistry registration)
    {
        Reloading = reloading;
        Host = host;
        Contracts = contracts;
        Registration = registration;
    }

    public bool Reloading { get; }

    public IExtensionHostBridge Host { get; }
    public IExtensionContractRegistry Contracts { get; }

    public IExtensionRegistration Registration { get; }
}
