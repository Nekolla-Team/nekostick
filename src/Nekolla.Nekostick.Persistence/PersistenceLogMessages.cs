using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Persistence;

internal static partial class PersistenceLogMessages
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Debug,
        Message = "Persistence validation was rejected. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void ValidationRejected(ILogger logger, string operation, string entityId);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Debug,
        Message = "Persistence operation was cancelled. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void OperationCancelled(ILogger logger, string operation, string entityId);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Persistence operation failed. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void OperationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string entityId);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Warning,
        Message = "Persistence operation conflicted. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void OperationConflict(
        ILogger logger,
        Exception exception,
        string operation,
        string entityId);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Error,
        Message = "Persistence startup failed. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void StartupFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string entityId);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Configuration change notification failed. Operation: {Operation}. Version: {Version}.")]
    internal static partial void ConfigurationNotificationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long version);

    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Warning,
        Message = "Port lease operation failed. Operation: {Operation}. NodeId: {NodeId}. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void PortLeaseFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string nodeId,
        Guid serviceId,
        int port);

    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Debug,
        Message = "Port lease operation was cancelled. Operation: {Operation}. NodeId: {NodeId}. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void PortLeaseCancelled(
        ILogger logger,
        string operation,
        string nodeId,
        Guid serviceId,
        int port);

    [LoggerMessage(
        EventId = 3009,
        Level = LogLevel.Warning,
        Message = "Runtime persistence operation failed. Operation: {Operation}. NodeId: {NodeId}. ServiceId: {ServiceId}.")]
    internal static partial void RuntimePersistenceFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string nodeId,
        Guid serviceId);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Debug,
        Message = "Runtime persistence operation was cancelled. Operation: {Operation}. NodeId: {NodeId}. ServiceId: {ServiceId}.")]
    internal static partial void RuntimePersistenceCancelled(
        ILogger logger,
        string operation,
        string nodeId,
        Guid serviceId);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Warning,
        Message = "Node state persistence operation failed. Operation: {Operation}. NodeId: {NodeId}.")]
    internal static partial void NodeStatePersistenceFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string nodeId);

    [LoggerMessage(
        EventId = 3014,
        Level = LogLevel.Debug,
        Message = "Node state persistence write conflict; retrying. Operation: {Operation}. NodeId: {NodeId}. Attempt: {Attempt}.")]
    internal static partial void NodeStatePersistenceRetrying(
        ILogger logger,
        Exception exception,
        string operation,
        string nodeId,
        int attempt);

    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Error,
        Message = "Configuration revision read failed during startup. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void ConfigurationRevisionReadFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string entityId);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Debug,
        Message = "Configuration semantic validation failed. Operation: {Operation}. EntityId: {EntityId}.")]
    internal static partial void SemanticValidationFailed(
        ILogger logger,
        string operation,
        string entityId);
}
