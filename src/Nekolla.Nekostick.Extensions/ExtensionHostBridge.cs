using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal sealed class ExtensionHostBridge : IExtensionHostBridge
{
    internal ExtensionHostBridge(
        HostApiVersion apiVersion,
        ExtensionSettingsConfiguration? settings,
        ExtensionTaskTracker tasks,
        ExtensionEventQueue events,
        ExtensionContractRegistry contracts,
        Action<ExtensionStatus> reportStatus,
        Action<ExtensionLogLevel, string> reportLog)
    {
        ApiVersion = apiVersion;
        Configuration = new ExtensionSettingsReader(settings);
        Contracts = contracts;
        Tasks = tasks;
        Events = events;
        Status = new ExtensionStatusSink(reportStatus);
        Logger = new ExtensionLogger(reportLog);
    }

    public HostApiVersion ApiVersion { get; }

    public IExtensionSettingsReader Configuration { get; }

    public IExtensionTaskScheduler Tasks { get; }

    public IExtensionEventPublisher Events { get; }
    public IExtensionContractRegistry Contracts { get; }

    public IExtensionStatusSink Status { get; }

    public IExtensionLogger Logger { get; }
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
