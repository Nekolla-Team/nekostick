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

}

internal static class HostLoggerCategory
{
    internal const string Startup = "Nekolla.Nekostick.Host.Startup";
    internal const string Routing = "Nekolla.Nekostick.Host.Routing";
    internal const string Supervision = "Nekolla.Nekostick.Host.Supervision";
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
        Level = LogLevel.Warning,
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
}

internal sealed class SafeConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new SafeConsoleLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class SafeConsoleLogger : ILogger
    {
        private readonly string _categoryName;

        public SafeConsoleLogger(string categoryName) => _categoryName = categoryName;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) =>
            (_categoryName == HostLoggerCategory.Supervision && logLevel >= LogLevel.Information) ||
            (logLevel >= LogLevel.Warning &&
                (_categoryName is HostLoggerCategory.Startup or HostLoggerCategory.Routing));

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
