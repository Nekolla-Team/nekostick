using System.Text.Json;
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
        object payload)
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

            runtimeManager.PublishCoreEvent(new ExtensionCoreEvent(kind, SchemaVersion, payloadJson));
        }
        catch (Exception)
        {
            // Core-event delivery is best effort and must never change the Host transition outcome.
        }
    }
}
