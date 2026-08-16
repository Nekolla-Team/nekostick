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
}

internal static class HostLoggerCategory
{
    internal const string Startup = "Nekolla.Nekostick.Host.Startup";
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
            logLevel >= LogLevel.Error &&
            string.Equals(_categoryName, HostLoggerCategory.Startup, StringComparison.Ordinal);

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
