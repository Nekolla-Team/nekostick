using System.Text.Json;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Defines deterministic fixture behavior selected by extension-owned JSON settings.</summary>
public sealed record FixtureMode(
        string Label,
        string HandlerId,
        bool StartFails,
        bool StopFails,
        bool PreviousStoppedFails,
        bool HandlerFails,
        bool RegisterFallback,
        bool DuplicateHandler,
        bool DuplicateFallback,
        bool StartTask,
        bool PublishOrderedEvents,
        bool PublishBoundedEvents,
        bool PublishCoreEvents,
        bool TypedContractExchange,
        bool IncludeFallbackCount,
        int EventCount,
        bool VerifyBridgeCapabilities,
        bool RequestLifecycleFromHandler,
        bool RequestLifecycleFromFallback,
        bool RequestLifecycleFromTask,
        bool RequestLifecycleFromEvent,
        bool UnregisterHandlerOnInvocation,
        bool UnregisterFallbackOnInvocation,
        bool ReregisterHandlerAfterUnregister,
        string? AttemptUnregisterHandlerId,
        bool AttemptUnregisterFallback,
        bool RequestLifecycleFromStart,
        bool RequestLifecycleFromPreviousStopped,
        bool RequestLifecycleFromStop,
        int LifecycleObservationPort,
        int UnregisterBarrierPort)
    {
        /// <summary>Reads the small test-only settings document.</summary>
        public static FixtureMode Parse(string? settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return Default;
            }

            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            return new FixtureMode(
                ReadString(root, "label", "fixture"),
                ReadString(root, "handlerId", "fixture.handler"),
                ReadBool(root, "startFails"),
                ReadBool(root, "stopFails"),
                ReadBool(root, "previousStoppedFails"),
                ReadBool(root, "handlerFails"),
                ReadBool(root, "registerFallback"),
                ReadBool(root, "duplicateHandler"),
                ReadBool(root, "duplicateFallback"),
                ReadBool(root, "startTask"),
                ReadBool(root, "publishOrderedEvents"),
                ReadBool(root, "publishBoundedEvents"),
                ReadBool(root, "publishCoreEvents"),
                ReadBool(root, "typedContractExchange"),
                ReadBool(root, "includeFallbackCount"),
                ReadInt(root, "eventCount", 3),
                ReadBool(root, "verifyBridgeCapabilities"),
                ReadBool(root, "requestLifecycleFromHandler"),
                ReadBool(root, "requestLifecycleFromFallback"),
                ReadBool(root, "requestLifecycleFromTask"),
                ReadBool(root, "requestLifecycleFromEvent"),
                ReadBool(root, "unregisterHandlerOnInvocation"),
                ReadBool(root, "unregisterFallbackOnInvocation"),
                ReadBool(root, "reregisterHandlerAfterUnregister"),
                ReadOptionalString(root, "attemptUnregisterHandlerId"),
                ReadBool(root, "attemptUnregisterFallback"),
                ReadBool(root, "requestLifecycleFromStart"),
                ReadBool(root, "requestLifecycleFromPreviousStopped"),
                ReadBool(root, "requestLifecycleFromStop"),
                ReadPort(root, "lifecycleObservationPort"),
                ReadPort(root, "unregisterBarrierPort"));
        }

        private static string ReadString(JsonElement root, string name, string fallback) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private static string? ReadOptionalString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool ReadBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static int ReadInt(JsonElement root, string name, int fallback) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result) &&
            result is > 0 and <= 1024
                ? result
                : fallback;
        private static int ReadPort(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result) &&
            result is > 0 and <= 65535
                ? result
                : 0;

        private static FixtureMode Default { get; } = new(
            "fixture",
            "fixture.handler",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            3,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            0,
            0);
    }