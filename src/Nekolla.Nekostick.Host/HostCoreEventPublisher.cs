using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

/// <summary>Publishes bounded, non-sensitive Host-owned core events without affecting transitions.</summary>
internal static class HostCoreEventPublisher
{
    private const int SchemaVersion = 1;
    private const int MaximumPayloadLength = 4096;

    internal static void Publish(
        ExtensionRuntimeManager? runtimeManager,
        ExtensionCoreEventKind kind,
        object payload,
        ILogger? logger = null) =>
        PublishCoreEvent(runtimeManager, kind, payload, targetExtensionId: null, logger: logger);

    internal static void Publish(
        ExtensionRuntimeManager? runtimeManager,
        ExtensionCoreEventKind kind,
        object payload,
        string targetExtensionId,
        ILogger? logger = null) =>
        PublishCoreEvent(runtimeManager, kind, payload, targetExtensionId, logger);

    private static void PublishCoreEvent(
        ExtensionRuntimeManager? runtimeManager,
        ExtensionCoreEventKind kind,
        object payload,
        string? targetExtensionId,
        ILogger? logger)
    {
        if (runtimeManager is null || payload is null)
        {
            return;
        }

        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            if (payloadJson.Length > MaximumPayloadLength)
            {
                return;
            }

            var @event = new ExtensionCoreEvent(kind, SchemaVersion, payloadJson);
            if (targetExtensionId is null)
            {
                runtimeManager.PublishCoreEvent(@event);
            }
            else
            {
                runtimeManager.PublishCoreEvent(@event, targetExtensionId);
            }
        }
        catch (Exception exception)
        {
            HostLogMessages.CoreEventDeliveryFailed(
                logger ?? HostLoggerDefaults.Logger,
                exception,
                kind);
            // Core-event delivery is best effort and must never change the Host transition outcome.
        }
    }
}
