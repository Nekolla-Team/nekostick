namespace Nekolla.Nekostick.Extensions;

// This is deliberately internal until the stable extension ABI is published in Contracts.
internal interface IInternalExtensionEntryMarker
{
}

// Deferred until the YAML dependency and its safe scalar/map/list policy are fixed.
internal interface IDeferredYamlManifestParser
{
    ManifestDiscoveryResult Parse(string canonicalRoot, string canonicalManifestPath);
}

// Future Host binding seam only. No implementation, DI access, HTTP access, or persistence is provided here.
internal interface IExtensionHostBridge
{
    IExtensionServiceScope Services { get; }

    IExtensionTaskScheduler Tasks { get; }

    IExtensionLogger Logger { get; }

    IExtensionStatusSink Status { get; }

    IExtensionEventPublisher Events { get; }

    IExtensionConfigurationReader Configuration { get; }
}

internal interface IExtensionServiceScope
{
}

internal interface IExtensionTaskScheduler
{
}

internal interface IExtensionLogger
{
}

internal interface IExtensionStatusSink
{
}

internal interface IExtensionEventPublisher
{
}

internal interface IExtensionConfigurationReader
{
}

internal interface IInternalExtensionEntry : IInternalExtensionEntryMarker
{
    ValueTask StartAsync(IExtensionHostBridge hostBridge, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}
