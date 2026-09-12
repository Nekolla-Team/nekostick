using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

internal static class HostEventIds
{
    internal static readonly EventId DatabaseStartupFailed = new(1001, "DatabaseStartupFailed");
    internal static readonly EventId ConfigurationRevisionReadFailed = new(1002, "ConfigurationRevisionReadFailed");
    internal static readonly EventId HostStartupFailed = new(1003, "HostStartupFailed");
    internal static readonly EventId ConfigurationSnapshotRejected = new(1004, "ConfigurationSnapshotRejected");
    internal static readonly EventId ConfigurationRefreshUnavailable = new(1005, "ConfigurationRefreshUnavailable");
    internal static readonly EventId NodeHeartbeatUnavailable = new(1006, "NodeHeartbeatUnavailable");
    internal static readonly EventId RouteRegexEvaluationTimedOut = new(1007, "RouteRegexEvaluationTimedOut");
    internal static readonly EventId AdmissionResourceRejected = new(1010, "AdmissionResourceRejected");
    internal static readonly EventId RouteOutcomeSummary = new(1011, "RouteOutcomeSummary");
    internal static readonly EventId StaticRejection = new(1012, "StaticRejection");
    internal static readonly EventId ProxyFailure = new(1013, "ProxyFailure");
    internal static readonly EventId ExtensionText = new(1014, "ExtensionText");
    internal static readonly EventId HostNodeActivityLost = new(1017, "HostNodeActivityLost");
    internal static readonly EventId ServiceLaunchMissingHostEnvironment = new(1018, "ServiceLaunchMissingHostEnvironment");
}

internal static class HostLoggerCategory
{
    internal const string Startup = "Nekolla.Nekostick.Host.Startup";
    internal const string Routing = "Nekolla.Nekostick.Host.Routing";
    internal const string Supervision = "Nekolla.Nekostick.Host.Supervision";
    internal const string Extensions = "Nekolla.Nekostick.Host.Extensions";
}
internal static class HostLoggerDefaults
{
    internal static ILogger Logger { get; } =
        new SafeConsoleLoggerProvider(LogLevel.Debug).CreateLogger(HostLoggerCategory.Startup);
}


internal static partial class HostLogMessages
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Database startup failed. Code: {ErrorCode}. Message: {SafeMessage}")]
    internal static partial void DatabaseStartupFailed(
        ILogger logger,
        StartupDatabaseErrorCode errorCode,
        string safeMessage);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Configuration revision read failed. Message: {SafeMessage}")]
    internal static partial void ConfigurationRevisionUnavailable(ILogger logger, string safeMessage);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Configuration revision read failed. Code: {ErrorCode}. Message: {SafeMessage}")]
    internal static partial void ConfigurationRevisionReadFailed(
        ILogger logger,
        ConfigurationErrorCode errorCode,
        string safeMessage);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "Complete configuration snapshot validation failed.")]
    internal static partial void ConfigurationSnapshotRejected(ILogger logger);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "Configuration refresh is unavailable.")]
    internal static partial void ConfigurationRefreshUnavailable(ILogger logger);
    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "Configuration snapshot manager completion failed after publication. Version: {Version}.")]
    internal static partial void ConfigurationSnapshotCompletionFailed(ILogger logger, long version);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "Node registration or heartbeat is unavailable.")]
    internal static partial void NodeHeartbeatUnavailable(ILogger logger);

    [LoggerMessage(
        EventId = 1017,
        Level = LogLevel.Critical,
        Message = "Host node activity lease could not be reacquired; another node may have taken ownership. NodeId: {NodeId}.")]
    internal static partial void HostNodeActivityLost(ILogger logger, string nodeId);

    [LoggerMessage(
        EventId = 1018,
        Level = LogLevel.Warning,
        Message = "Service launch failed because a required host environment placeholder is missing. ServiceId: {ServiceId}. Version: {Version}. Placeholder: {Placeholder}.")]
    internal static partial void ServiceLaunchMissingHostEnvironment(
        ILogger logger,
        Guid serviceId,
        long version,
        string placeholder);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Warning,
        Message = "Route matching regex evaluation timed out. RouteIds: {RouteIds}. Count: {Count}.")]
    internal static partial void RouteRegexEvaluationTimedOut(
        ILogger logger,
        Guid[] routeIds,
        int count);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Warning,
        Message = "Request admission or resource limit rejected. FailureKind: {FailureKind}. StatusCode: {StatusCode}. RetryAfterPresent: {RetryAfterPresent}. RetryAfterSeconds: {RetryAfterSeconds}. RouteId: {RouteId}. TargetType: {TargetType}.")]
    internal static partial void AdmissionResourceRejected(
        ILogger logger,
        HostRequestAdmissionFailureKind failureKind,
        int statusCode,
        bool retryAfterPresent,
        int? retryAfterSeconds,
        Guid? routeId,
        RouteTargetType? targetType);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Debug,
        Message = "Matched route target completed. RouteId: {RouteId}. TargetType: {TargetType}. Outcome: {Outcome}. StatusCode: {StatusCode}. ServiceId: {ServiceId}.")]
    internal static partial void RouteOutcomeSummary(
        ILogger logger,
        Guid routeId,
        RouteTargetType targetType,
        RouteTargetExecutionResult outcome,
        int statusCode,
        Guid? serviceId);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Warning,
        Message = "Static route target rejected. RouteId: {RouteId}. TargetType: {TargetType}. Outcome: {Outcome}. StatusCode: {StatusCode}.")]
    internal static partial void StaticRejection(
        ILogger logger,
        Guid routeId,
        RouteTargetType targetType,
        RouteTargetExecutionResult outcome,
        int statusCode);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Warning,
        Message = "Proxy route target failed. RouteId: {RouteId}. ServiceId: {ServiceId}. TargetType: {TargetType}. Outcome: {Outcome}. StatusCode: {StatusCode}.")]
    internal static partial void ProxyFailure(
        ILogger logger,
        Guid routeId,
        Guid serviceId,
        RouteTargetType targetType,
        RouteTargetExecutionResult outcome,
        int statusCode);

    [LoggerMessage(
        EventId = 1099,
        Level = LogLevel.Debug,
        Message = "Failure details. Operation: {Operation}.")]
    internal static partial void FailureDetails(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Service launch accepted. ServiceId: {ServiceId}. InstanceId: {InstanceId}. ProcessId: {ProcessId}.")]
    internal static partial void ServiceLaunchAccepted(ILogger logger, Guid serviceId, Guid instanceId, int processId);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Service startup failed. ServiceId: {ServiceId}. Version: {Version}.")]
    internal static partial void ServiceLaunchRejected(ILogger logger, Guid serviceId, long version);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Information,
        Message = "Service ready. ServiceId: {ServiceId}. Version: {Version}.")]
    internal static partial void ServiceReady(ILogger logger, Guid serviceId, long version);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Information,
        Message = "Service process exited successfully. ServiceId: {ServiceId}.")]
    internal static partial void ServiceExitedSuccessfully(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Warning,
        Message = "Service process exited unexpectedly. ServiceId: {ServiceId}.")]
    internal static partial void ServiceExitedUnexpectedly(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Information,
        Message = "Service stopped. ServiceId: {ServiceId}.")]
    internal static partial void ServiceStopped(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 1107,
        Level = LogLevel.Warning,
        Message = "Service restart scheduled. ServiceId: {ServiceId}.")]
    internal static partial void ServiceRestartScheduled(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 1108,
        Level = LogLevel.Information,
        Message = "Node registered. NodeId: {NodeId}.")]
    internal static partial void NodeRegistered(ILogger logger, string nodeId);

    [LoggerMessage(
        EventId = 1109,
        Level = LogLevel.Information,
        Message = "Configuration snapshot applied. Version: {Version}.")]
    internal static partial void ConfigurationSnapshotApplied(ILogger logger, long version);

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Information,
        Message = "Host listening on: {ListenUrl}")]
    internal static partial void NowListening(ILogger logger, string listenUrl);

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Information,
        Message = "Nekostick initialization finished. Press Ctrl+C to shutdown.")]
    internal static partial void ApplicationStarted(ILogger logger);

    [LoggerMessage(
        EventId = 1019,
        Level = LogLevel.Warning,
        Message = "Extension node state persistence was not applied; node-reported extension states may be stale.")]
    internal static partial void NodeStatePersistenceFailed(ILogger logger, Exception? exception);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Warning,
        Message = "Host node activity lock still held on reconnect (attempt {Attempt}); tolerating a possible zombie session before declaring a takeover.")]
    internal static partial void HostNodeActivityContended(ILogger logger, int attempt);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Warning,
        Message = "Extension directory skipped during scan. Directory: {Directory}. Code: {FailureCode}.")]
    internal static partial void ExtensionDirectorySkipped(ILogger logger, string directory, string failureCode);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Warning,
        Message = "Duplicate extension manifest id during scan. ExtensionId: {ExtensionId}. The conflicting directory was skipped.")]
    internal static partial void DuplicateExtensionManifestId(ILogger logger, string extensionId);
    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Warning,
        Message = "Diagnostic report serialization failed. Report: {Report}.")]
    internal static partial void DiagnosticSerializationFailed(
        ILogger logger,
        Exception exception,
        string report);

    [LoggerMessage(
        EventId = 1031,
        Level = LogLevel.Warning,
        Message = "Extension capability read failed. Operation: {Operation}. ExtensionId: {ExtensionId}. ResourceId: {ResourceId}.")]
    internal static partial void ExtensionCapabilityReadFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string? extensionId,
        Guid? resourceId);

    [LoggerMessage(
        EventId = 1032,
        Level = LogLevel.Warning,
        Message = "Extension service lifecycle operation failed. ServiceId: {ServiceId}.")]
    internal static partial void ExtensionServiceLifecycleFailed(
        ILogger logger,
        Exception exception,
        Guid serviceId);

    [LoggerMessage(
        EventId = 1033,
        Level = LogLevel.Warning,
        Message = "Extension content digest computation failed. ExtensionId: {ExtensionId}.")]
    internal static partial void ExtensionContentDigestFailed(
        ILogger logger,
        Exception exception,
        string extensionId);

    [LoggerMessage(
        EventId = 1034,
        Level = LogLevel.Warning,
        Message = "Executable route construction failed. RouteCount: {RouteCount}.")]
    internal static partial void ExecutableRouteBuildFailed(
        ILogger logger,
        Exception exception,
        int routeCount);

    [LoggerMessage(
        EventId = 1035,
        Level = LogLevel.Warning,
        Message = "Extension HTTP adapter operation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void ExtensionHttpAdapterFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 1036,
        Level = LogLevel.Warning,
        Message = "Extension management operation failed. Operation: {Operation}. CallerExtensionId: {CallerExtensionId}. TargetExtensionId: {TargetExtensionId}. ErrorCode: {ErrorCode}.")]
    internal static partial void ExtensionManagementFailure(
        ILogger logger,
        string operation,
        string callerExtensionId,
        string? targetExtensionId,
        ConfigurationErrorCode errorCode);

    [LoggerMessage(
        EventId = 1037,
        Level = LogLevel.Debug,
        Message = "Extension management operation was rejected. Operation: {Operation}. CallerExtensionId: {CallerExtensionId}. TargetExtensionId: {TargetExtensionId}. ErrorCode: {ErrorCode}.")]
    internal static partial void ExtensionManagementRejected(
        ILogger logger,
        string operation,
        string callerExtensionId,
        string? targetExtensionId,
        ConfigurationErrorCode errorCode);

    [LoggerMessage(
        EventId = 1038,
        Level = LogLevel.Warning,
        Message = "Extension management operation raised an exception. Operation: {Operation}. CallerExtensionId: {CallerExtensionId}. TargetExtensionId: {TargetExtensionId}.")]
    internal static partial void ExtensionManagementException(
        ILogger logger,
        Exception exception,
        string operation,
        string callerExtensionId,
        string? targetExtensionId);

    [LoggerMessage(
        EventId = 1039,
        Level = LogLevel.Warning,
        Message = "Extension management background operation failed. Operation: {Operation}. CallerExtensionId: {CallerExtensionId}.")]
    internal static partial void ExtensionManagementBackgroundFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string callerExtensionId);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Warning,
        Message = "Extension scan failed. Operation: {Operation}.")]
    internal static partial void ExtensionScanFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1041,
        Level = LogLevel.Warning,
        Message = "Configuration publication cleanup failed. Operation: {Operation}.")]
    internal static partial void ConfigurationPublicationCleanupFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1042,
        Level = LogLevel.Warning,
        Message = "Configuration snapshot validation failed. Operation: {Operation}.")]
    internal static partial void SnapshotValidationFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1043,
        Level = LogLevel.Warning,
        Message = "Configuration snapshot retirement cleanup failed. Operation: {Operation}.")]
    internal static partial void SnapshotRetirementCleanupFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1044,
        Level = LogLevel.Warning,
        Message = "Host core-event delivery failed. EventKind: {EventKind}.")]
    internal static partial void CoreEventDeliveryFailed(
        ILogger logger,
        Exception exception,
        ExtensionCoreEventKind eventKind);

    [LoggerMessage(
        EventId = 1046,
        Level = LogLevel.Warning,
        Message = "Process output logging failed. Operation: {Operation}.")]
    internal static partial void ProcessOutputSinkFailure(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1047,
        Level = LogLevel.Debug,
        Message = "Process output cleanup logging failed. Operation: {Operation}.")]
    internal static partial void ProcessOutputSinkCleanupFailure(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1048,
        Level = LogLevel.Warning,
        Message = "Lifecycle background operation failed. Operation: {Operation}. ServiceId: {ServiceId}.")]
    internal static partial void LifecycleBackgroundFailed(
        ILogger logger,
        Exception exception,
        string operation,
        Guid serviceId);

    [LoggerMessage(
        EventId = 1049,
        Level = LogLevel.Debug,
        Message = "Lifecycle background operation was cancelled. Operation: {Operation}. ServiceId: {ServiceId}.")]
    internal static partial void LifecycleBackgroundCancelled(
        ILogger logger,
        string operation,
        Guid serviceId);
    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Debug,
        Message = "Extension HTTP adapter operation was cancelled. Operation: {Operation}.")]
    internal static partial void ExtensionHttpAdapterCancelled(ILogger logger, string operation);
    [LoggerMessage(
        EventId = 1051,
        Level = LogLevel.Debug,
        Message = "Static target alignment check failed. Operation: {Operation}.")]
    internal static partial void StaticTargetAlignmentFailed(
        ILogger logger,
        Exception exception,
        string operation);
    [LoggerMessage(
        EventId = 1071,
        Level = LogLevel.Warning,
        Message = "Service endpoint publication failed; the published endpoint snapshot may be stale. NodeId: {NodeId}. Occurrences: {Occurrences}.")]
    internal static partial void EndpointPublicationFailed(
        ILogger logger,
        Exception exception,
        string nodeId,
        long occurrences);
    [LoggerMessage(
        EventId = 1052,
        Level = LogLevel.Warning,
        Message = "Route event observation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void RouteEventObservationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 1053,
        Level = LogLevel.Debug,
        Message = "Lifecycle shutdown cleanup was skipped. Operation: {Operation}.")]
    internal static partial void LifecycleShutdownCleanupSkipped(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1079,
        Level = LogLevel.Debug,
        Message = "Host node activity cleanup was skipped during shutdown. Operation: {Operation}.")]
    internal static partial void NodeActivityCleanupFailure(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1054,
        Level = LogLevel.Information,
        Message = "Extension enabled. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionEnabled(
        ILogger logger,
        string callerExtensionId,
        string extensionId,
        long? version);

    [LoggerMessage(
        EventId = 1055,
        Level = LogLevel.Debug,
        Message = "Extension disable was already satisfied. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}. State: Disabled.")]
    internal static partial void ExtensionDisableNoOp(
        ILogger logger,
        string callerExtensionId,
        string extensionId);

    [LoggerMessage(
        EventId = 1056,
        Level = LogLevel.Information,
        Message = "Extension disabled. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionDisabled(
        ILogger logger,
        string callerExtensionId,
        string extensionId,
        long? version);

    [LoggerMessage(
        EventId = 1057,
        Level = LogLevel.Information,
        Message = "Extension reload published. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionReloadPublished(
        ILogger logger,
        string callerExtensionId,
        string extensionId,
        long version);

    [LoggerMessage(
        EventId = 1058,
        Level = LogLevel.Warning,
        Message = "Extension reload target unavailable. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}.")]
    internal static partial void ExtensionReloadTargetUnavailable(
        ILogger logger,
        string callerExtensionId,
        string extensionId);

    [LoggerMessage(
        EventId = 1059,
        Level = LogLevel.Warning,
        Message = "Extension reload failed. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}.")]
    internal static partial void ExtensionReloadFailed(
        ILogger logger,
        string callerExtensionId,
        string extensionId);

    [LoggerMessage(
        EventId = 1060,
        Level = LogLevel.Debug,
        Message = "Extension reload queued. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}.")]
    internal static partial void ExtensionReloadQueued(
        ILogger logger,
        string callerExtensionId,
        string extensionId);

    [LoggerMessage(
        EventId = 1061,
        Level = LogLevel.Information,
        Message = "Extension record deleted. CallerExtensionId: {CallerExtensionId}. ExtensionId: {ExtensionId}. Version: {Version}.")]
    internal static partial void ExtensionRecordDeleted(
        ILogger logger,
        string callerExtensionId,
        string extensionId,
        long? version);

    [LoggerMessage(
        EventId = 1062,
        Level = LogLevel.Information,
        Message = "Extension refresh completed. CallerExtensionId: {CallerExtensionId}. Added: {Added}. VersionUpdated: {VersionUpdated}. Missing: {Missing}. Skipped: {Skipped}.")]
    internal static partial void ExtensionRefreshCompleted(
        ILogger logger,
        string callerExtensionId,
        int added,
        int versionUpdated,
        int missing,
        int skipped);

    [LoggerMessage(
        EventId = 1063,
        Level = LogLevel.Warning,
        Message = "Prior extension generation reused. Reason: {Reason}. GenerationId: {GenerationId}.")]
    internal static partial void PriorGenerationReused(
        ILogger logger,
        string reason,
        long generationId);

    [LoggerMessage(
        EventId = 1064,
        Level = LogLevel.Warning,
        Message = "Unsafe unavailable binding triggered fallback. GenerationId: {GenerationId}. FallbackPublished: {FallbackPublished}.")]
    internal static partial void UnsafeUnavailableBindingFallback(
        ILogger logger,
        long generationId,
        bool fallbackPublished);

    [LoggerMessage(
        EventId = 1065,
        Level = LogLevel.Warning,
        Message = "Generation readiness failed; fallback attempted. FailureCode: {FailureCode}. FallbackPublished: {FallbackPublished}.")]
    internal static partial void GenerationReadyFallback(
        ILogger logger,
        string failureCode,
        bool fallbackPublished);

    [LoggerMessage(
        EventId = 1066,
        Level = LogLevel.Warning,
        Message = "Configuration fallback published. Mode: {Mode}. Version: {Version}.")]
    internal static partial void ConfigurationFallbackPublished(
        ILogger logger,
        string mode,
        long version);

    [LoggerMessage(
        EventId = 1067,
        Level = LogLevel.Warning,
        Message = "Extension binding quarantined. ExtensionId: {ExtensionId}. Code: {Code}.")]
    internal static partial void ExtensionBindingQuarantined(
        ILogger logger,
        string extensionId,
        string code);
    [LoggerMessage(
        EventId = 1068,
        Level = LogLevel.Debug,
        Message = "Port lease operation result. Status: {Status}. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void PortLeaseOperationResult(
        ILogger logger,
        PersistencePortLeaseOperationStatus status,
        Guid serviceId,
        int port);

    [LoggerMessage(
        EventId = 1069,
        Level = LogLevel.Debug,
        Message = "Port lease operation rejected while new leases are disabled. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void PortLeaseNewLeasesRejected(
        ILogger logger,
        Guid serviceId,
        int port);

    [LoggerMessage(
        EventId = 1070,
        Level = LogLevel.Debug,
        Message = "Node heartbeat updated. NodeId: {NodeId}. ConfigurationVersion: {ConfigurationVersion}.")]
    internal static partial void NodeHeartbeatUpdated(
        ILogger logger,
        string nodeId,
        long configurationVersion);
}

internal sealed class SafeConsoleLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minimumLevel;

    /// <summary>Creates the stderr sink with the configured minimum level.</summary>
    public SafeConsoleLoggerProvider(LogLevel minimumLevel = LogLevel.Information) =>
        _minimumLevel = minimumLevel;

    public ILogger CreateLogger(string categoryName) =>
        new SafeConsoleLogger(categoryName, _minimumLevel);

    public void Dispose()
    {
    }

    private sealed class SafeConsoleLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly LogLevel _minimumLevel;

        public SafeConsoleLogger(string categoryName, LogLevel minimumLevel)
        {
            _categoryName = categoryName;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
        {
            // Category policy is decided by the factory-level filters; the provider only
            // enforces the configured minimum.
            return logLevel >= _minimumLevel && logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var safeMessage = formatter(state, null);
            Console.Error.WriteLine($"HOST_EVENT {eventId.Id}: {safeMessage}");
            if (exception is not null)
            {
                Console.Error.WriteLine(exception.ToString());
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
