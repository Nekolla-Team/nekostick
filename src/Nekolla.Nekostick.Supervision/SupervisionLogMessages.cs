using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Supervision;

internal static partial class SupervisionLogMessages
{
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "Supervisor operation failed. Operation: {Operation}. ServiceId: {ServiceId}.")]
    internal static partial void OperationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        Guid serviceId);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Debug,
        Message = "Supervisor operation was cancelled. Operation: {Operation}. ServiceId: {ServiceId}.")]
    internal static partial void OperationCancelled(ILogger logger, string operation, Guid serviceId);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Warning,
        Message = "Supervisor process liveness check failed. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessLivenessFailed(
        ILogger logger,
        Exception exception,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Debug,
        Message = "Supervisor process timestamp normalization failed. ServiceId: {ServiceId}.")]
    internal static partial void ProcessTimestampNormalizationFailed(
        ILogger logger,
        Exception exception,
        Guid serviceId);

    [LoggerMessage(
        EventId = 5005,
        Level = LogLevel.Warning,
        Message = "Supervisor lease release failed. NodeId: {NodeId}. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void LeaseReleaseFailed(
        ILogger logger,
        Exception exception,
        string nodeId,
        Guid serviceId,
        int port);

    [LoggerMessage(
        EventId = 5006,
        Level = LogLevel.Warning,
        Message = "Supervisor health probe failed. ServiceId: {ServiceId}.")]
    internal static partial void HealthProbeFailed(
        ILogger logger,
        Exception exception,
        Guid serviceId);

    [LoggerMessage(
        EventId = 5007,
        Level = LogLevel.Debug,
        Message = "Process cleanup timed out. Operation: {Operation}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessCleanupTimedOut(ILogger logger, string operation, string instanceId);

    [LoggerMessage(
        EventId = 5008,
        Level = LogLevel.Warning,
        Message = "Process cleanup failed. Operation: {Operation}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessCleanupFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string instanceId);

    [LoggerMessage(
        EventId = 5009,
        Level = LogLevel.Debug,
        Message = "Process start was rejected by validation. Operation: {Operation}. ServiceId: {ServiceId}.")]
    internal static partial void ProcessStartValidationRejected(ILogger logger, string operation, Guid serviceId);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Debug,
        Message = "Process start was cancelled. ServiceId: {ServiceId}.")]
    internal static partial void ProcessStartCancelled(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 5011,
        Level = LogLevel.Warning,
        Message = "Process start failed. ServiceId: {ServiceId}.")]
    internal static partial void ProcessStartFailed(ILogger logger, Exception exception, Guid serviceId);

    [LoggerMessage(
        EventId = 5012,
        Level = LogLevel.Debug,
        Message = "Process stop timed out before force termination. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessStopTimedOut(
        ILogger logger,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5013,
        Level = LogLevel.Debug,
        Message = "Process stop was cancelled before force termination. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessStopCancelled(
        ILogger logger,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5014,
        Level = LogLevel.Warning,
        Message = "Process monitor failed. Operation: {Operation}. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessMonitorFailed(
        ILogger logger,
        Exception exception,
        string operation,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5015,
        Level = LogLevel.Warning,
        Message = "Process output capture failed. Stream: {Stream}. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessOutputCaptureFailed(
        ILogger logger,
        Exception exception,
        string stream,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5016,
        Level = LogLevel.Debug,
        Message = "Process exit code could not be read. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessExitCodeReadFailed(
        ILogger logger,
        Exception exception,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5017,
        Level = LogLevel.Warning,
        Message = "Process exit observer failed. ServiceId: {ServiceId}. InstanceId: {InstanceId}.")]
    internal static partial void ProcessExitObserverFailed(
        ILogger logger,
        Exception exception,
        Guid serviceId,
        string instanceId);

    [LoggerMessage(
        EventId = 5018,
        Level = LogLevel.Warning,
        Message = "Process signal operation failed. Operation: {Operation}. ProcessId: {ProcessId}. Signal: {Signal}.")]
    internal static partial void ProcessSignalFailed(
        ILogger logger,
        Exception exception,
        string operation,
        int processId,
        int signal);

    [LoggerMessage(
        EventId = 5019,
        Level = LogLevel.Debug,
        Message = "Health probe was cancelled. ServiceId: {ServiceId}.")]
    internal static partial void HealthProbeCancelled(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Debug,
        Message = "Health probe timed out. ServiceId: {ServiceId}.")]
    internal static partial void HealthProbeTimedOut(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 5021,
        Level = LogLevel.Warning,
        Message = "Health probe failed. ServiceId: {ServiceId}.")]
    internal static partial void HealthProbeOperationFailed(ILogger logger, Exception exception, Guid serviceId);

    [LoggerMessage(
        EventId = 5022,
        Level = LogLevel.Debug,
        Message = "Supervisor operation was rejected by validation. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void OperationValidationRejected(ILogger logger, string operation, string entityId);
    [LoggerMessage(
        EventId = 5023,
        Level = LogLevel.Information,
        Message = "Service stop was cancelled. ServiceId: {ServiceId}.")]
    internal static partial void StopCancelled(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 5024,
        Level = LogLevel.Information,
        Message = "Service stop completed. ServiceId: {ServiceId}.")]
    internal static partial void StopCompleted(ILogger logger, Guid serviceId);

    [LoggerMessage(
        EventId = 5025,
        Level = LogLevel.Information,
        Message = "Initial service lease released. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void InitialLeaseReleased(ILogger logger, Guid serviceId, int port);
}
