using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Identifies a safe, non-sensitive upstream failure stage.</summary>
internal enum MicroserviceProxyFailureStage
{
    Connection,
    UpstreamDisconnect,
    Timeout,
    Request,
    Response,
    Cancellation,
    Unknown
}

internal static class MicroserviceProxyTelemetry
{
    private static readonly EventId AttemptFailedEvent =
        new(2001, "ProxyAttemptFailed");

    private static readonly Action<ILogger, Guid?, Guid, int, MicroserviceProxyFailureStage, long, Exception?> LogAttempt =
        LoggerMessage.Define<Guid?, Guid, int, MicroserviceProxyFailureStage, long>(
            LogLevel.Warning,
            AttemptFailedEvent,
            "Proxy attempt failed. RouteId: {RouteId}. ServiceId: {ServiceId}. Attempt: {Attempt}. FailureStage: {FailureStage}. ElapsedMilliseconds: {ElapsedMilliseconds}.");

    private static readonly Action<ILogger, Guid?, Guid, int, MicroserviceProxyFailureStage, long, Exception?> LogAttemptDetails =
        LoggerMessage.Define<Guid?, Guid, int, MicroserviceProxyFailureStage, long>(
            LogLevel.Debug,
            new EventId(2002, "ProxyAttemptFailureDetails"),
            "Proxy attempt failure details. RouteId: {RouteId}. ServiceId: {ServiceId}. Attempt: {Attempt}. FailureStage: {FailureStage}. ElapsedMilliseconds: {ElapsedMilliseconds}.");

    internal static void AttemptFailed(
        ILogger logger,
        Guid? routeId,
        Guid serviceId,
        int attempt,
        MicroserviceProxyFailureStage stage,
        long elapsedMilliseconds,
        Exception? exception = null)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            LogAttempt(logger, routeId, serviceId, attempt, stage, elapsedMilliseconds, null);
        }

        if (exception is not null && logger.IsEnabled(LogLevel.Debug))
        {
            LogAttemptDetails(logger, routeId, serviceId, attempt, stage, elapsedMilliseconds, exception);
        }
    }
}
