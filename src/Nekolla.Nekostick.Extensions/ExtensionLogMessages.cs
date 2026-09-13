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

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Warning,
        Message = "Extension lifecycle operation failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionLifecycleOperationFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Debug,
        Message = "Extension lifecycle operation was cancelled. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionLifecycleOperationCancelled(
        ILogger logger,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2012,
        Level = LogLevel.Warning,
        Message = "Extension lifecycle state publication failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionLifecyclePublicationFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2013,
        Level = LogLevel.Error,
        Message = "Extension generation commit failed. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationCommitFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2014,
        Level = LogLevel.Debug,
        Message = "Extension generation commit was cancelled. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationCommitCancelled(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2015,
        Level = LogLevel.Error,
        Message = "Extension generation preparation failed. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationPreparationFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2016,
        Level = LogLevel.Debug,
        Message = "Extension generation preparation was cancelled. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationPreparationCancelled(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2017,
        Level = LogLevel.Warning,
        Message = "Extension candidate abort failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationCandidateAbortFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2018,
        Level = LogLevel.Warning,
        Message = "Extension generation context release failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationContextReleaseFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2019,
        Level = LogLevel.Warning,
        Message = "Detached extension instance release failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionInstanceReleaseFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Warning,
        Message = "Extension operation failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionOperationFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2021,
        Level = LogLevel.Debug,
        Message = "Extension operation was cancelled. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionOperationCancelled(
        ILogger logger,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2022,
        Level = LogLevel.Warning,
        Message = "Extension streaming request was rejected. ExtensionId: {ExtensionId}. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionStreamingRequestRejected(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 2023,
        Level = LogLevel.Debug,
        Message = "Extension streaming request body dispose failed. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionStreamingBodyDisposeFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2024,
        Level = LogLevel.Debug,
        Message = "Extension streaming request was cancelled. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionStreamingRequestCancelled(
        ILogger logger,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2025,
        Level = LogLevel.Warning,
        Message = "Extension handler invocation failed. ExtensionId: {ExtensionId}. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionHandlerInvocationFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 2026,
        Level = LogLevel.Debug,
        Message = "Extension handler invocation was cancelled. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionHandlerInvocationCancelled(
        ILogger logger,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2027,
        Level = LogLevel.Warning,
        Message = "Extension fallback invocation failed. ExtensionId: {ExtensionId}. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionFallbackInvocationFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 2028,
        Level = LogLevel.Debug,
        Message = "Extension streaming request read timed out. ExtensionId: {ExtensionId}. Operation: {Operation}.")]
    internal static partial void ExtensionStreamingReadTimedOut(
        ILogger logger,
        string extensionId,
        string operation);

    [LoggerMessage(
        EventId = 2029,
        Level = LogLevel.Warning,
        Message = "Extension event queue notification failed. Operation: {Operation}.")]
    internal static partial void ExtensionEventQueueNotificationFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2030,
        Level = LogLevel.Debug,
        Message = "Extension event subscriber cancellation was ignored. Operation: {Operation}.")]
    internal static partial void ExtensionEventSubscriberCancelled(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2031,
        Level = LogLevel.Warning,
        Message = "Extension failure callback failed. Operation: {Operation}.")]
    internal static partial void ExtensionFailureCallbackFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2032,
        Level = LogLevel.Warning,
        Message = "Tracked extension task did not complete before its stop timeout. Operation: {Operation}.")]
    internal static partial void ExtensionTaskStopTimedOut(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2033,
        Level = LogLevel.Warning,
        Message = "Extension route event publication failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionRouteEventPublicationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 2034,
        Level = LogLevel.Warning,
        Message = "Extension route callback failed. ExtensionId: {ExtensionId}. Operation: {Operation}. FailureCode: {FailureCode}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionRouteCallbackFailed(
        ILogger logger,
        Exception exception,
        string extensionId,
        string operation,
        string failureCode,
        long occurrences);

    [LoggerMessage(
        EventId = 2035,
        Level = LogLevel.Warning,
        Message = "Extension assembly unload was not confirmed. Operation: {Operation}.")]
    internal static partial void ExtensionUnloadNotConfirmed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2036,
        Level = LogLevel.Debug,
        Message = "Extension host info snapshot was unavailable. Operation: {Operation}.")]
    internal static partial void ExtensionHostInfoUnavailable(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2037,
        Level = LogLevel.Debug,
        Message = "Extension directory or manifest path was rejected. Operation: {Operation}.")]
    internal static partial void ExtensionPathRejected(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2038,
        Level = LogLevel.Debug,
        Message = "Extension dependency graph materialization failed. Operation: {Operation}.")]
    internal static partial void ExtensionDependencyGraphFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2039,
        Level = LogLevel.Debug,
        Message = "Extension contract assembly identity was rejected. Operation: {Operation}.")]
    internal static partial void ExtensionContractIdentityRejected(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2040,
        Level = LogLevel.Warning,
        Message = "Extension route hook failed closed. Stage: {Stage}. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionRouteHookFailed(
        ILogger logger,
        Exception exception,
        string stage,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 2041,
        Level = LogLevel.Warning,
        Message = "Extension generation release failed. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationReleaseFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2042,
        Level = LogLevel.Debug,
        Message = "Extension generation retirement timed out waiting for leases to drain. Operation: {Operation}.")]
    internal static partial void ExtensionGenerationRetireTimedOut(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2043,
        Level = LogLevel.Debug,
        Message = "Extension route dispatch cancellation was skipped because the source was already disposed. Operation: {Operation}.")]
    internal static partial void ExtensionRouteDispatchCancelSkipped(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2044,
        Level = LogLevel.Information,
        Message = "Extension generation contexts retained. Reused: {Reused}. Count: {Count}. ExtensionIds: {ExtensionIds}.")]
    internal static partial void ExtensionGenerationContextsRetained(
        ILogger logger,
        bool reused,
        int count,
        string extensionIds);

    [LoggerMessage(
        EventId = 2045,
        Level = LogLevel.Information,
        Message = "Extension generation candidates started. Reused: {Reused}. Count: {Count}. ExtensionIds: {ExtensionIds}.")]
    internal static partial void ExtensionGenerationCandidatesStarted(
        ILogger logger,
        bool reused,
        int count,
        string extensionIds);

    [LoggerMessage(
        EventId = 2046,
        Level = LogLevel.Information,
        Message = "Extension generation context released. GenerationId: {GenerationId}. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionGenerationContextReleased(
        ILogger logger,
        string extensionId,
        string version,
        long? generationId);

    [LoggerMessage(
        EventId = 2047,
        Level = LogLevel.Warning,
        Message = "Extension generation preparation aborted. GenerationId: {GenerationId}. CandidateCount: {CandidateCount}.")]
    internal static partial void ExtensionGenerationPreparationAborted(
        ILogger logger,
        long generationId,
        int candidateCount);

    [LoggerMessage(
        EventId = 2048,
        Level = LogLevel.Debug,
        Message = "Extension assembly shadow link was unavailable; loading from the real path. ExtensionId: {ExtensionId}.")]
    internal static partial void ExtensionAssemblyShadowLinkUnavailable(
        ILogger logger,
        Exception exception,
        string extensionId);

    [LoggerMessage(
        EventId = 2049,
        Level = LogLevel.Warning,
        Message = "Extension assembly shadow link cleanup failed. Operation: {Operation}.")]
    internal static partial void ExtensionAssemblyShadowLinkCleanupFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 2050,
        Level = LogLevel.Warning,
        Message = "Extension reported an unhealthy status. ExtensionId: {ExtensionId}. Kind: {Kind}. Code: {Code}.")]
    internal static partial void ExtensionReportedUnhealthyStatus(
        ILogger logger,
        string extensionId,
        string kind,
        string code);

    [LoggerMessage(
        EventId = 2051,
        Level = LogLevel.Information,
        Message = "Extension reported a healthy status again. ExtensionId: {ExtensionId}.")]
    internal static partial void ExtensionReportedHealthyStatus(
        ILogger logger,
        string extensionId);
}
