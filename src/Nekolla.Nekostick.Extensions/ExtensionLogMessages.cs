using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Safe lifecycle events for extension runtime transitions.</summary>
internal static partial class ExtensionLogMessages
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Extension loaded. ExtensionId: {ExtensionId}. Version: {Version}. Handlers: {HandlerCount}. Fallback: {HasFallback}.")]
    internal static partial void ExtensionLoaded(
        ILogger logger,
        string extensionId,
        string version,
        int handlerCount,
        bool hasFallback);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Extension candidate failed. ExtensionId: {ExtensionId}. Code: {FailureCode}.")]
    internal static partial void ExtensionCandidateFailed(
        ILogger logger,
        string extensionId,
        string failureCode);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Debug,
        Message = "Extension failure details. ExtensionId: {ExtensionId}. Code: {FailureCode}.")]
    internal static partial void ExtensionFailureDetails(
        ILogger logger,
        Exception exception,
        string extensionId,
        string failureCode);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Extension unloaded. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionUnloaded(ILogger logger, string extensionId, string version);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Extension stopped after repeated failures. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionStoppedAfterFailures(
        ILogger logger,
        string extensionId,
        string version);
}
