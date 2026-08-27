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
}

internal static class HostLoggerCategory
{
    internal const string Startup = "Nekolla.Nekostick.Host.Startup";
    internal const string Routing = "Nekolla.Nekostick.Host.Routing";
    internal const string Supervision = "Nekolla.Nekostick.Host.Supervision";
    internal const string Extensions = "Nekolla.Nekostick.Host.Extensions";
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
