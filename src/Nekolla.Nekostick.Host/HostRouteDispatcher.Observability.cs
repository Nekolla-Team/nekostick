using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

internal sealed partial class HostRouteDispatcher
{
    private void LogAdmissionRejection(
        HostRequestAdmissionFailure failure,
        Guid? routeId,
        RouteTargetType? targetType) =>
        HostLogMessages.AdmissionResourceRejected(
            _logger,
            failure.Kind,
            failure.StatusCode,
            failure.RetryAfterSeconds.HasValue,
            failure.RetryAfterSeconds,
            routeId,
            targetType);

    private void LogMatchedTargetOutcome(
        RouteConfiguration? route,
        RouteMatch match,
        RouteTargetExecutionResult outcome,
        int statusCode)
    {
        var routeId = match.RouteId;
        var targetType = route?.Target.Type ?? match.Target?.Type;
        if (targetType is not { } selectedTargetType)
        {
            return;
        }

        if (outcome != RouteTargetExecutionResult.Handled)
        {
            switch (selectedTargetType)
            {
                case RouteTargetType.StaticFile:
                    HostLogMessages.StaticRejection(_logger, routeId, selectedTargetType, outcome, statusCode);
                    break;
                case RouteTargetType.Microservice when GetServiceId(route, match) is Guid serviceId:
                    HostLogMessages.ProxyFailure(
                        _logger,
                        routeId,
                        serviceId,
                        selectedTargetType,
                        outcome,
                        statusCode);
                    break;
            }
        }

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var summaryServiceId = GetServiceId(route, match);
        HostLogMessages.RouteOutcomeSummary(
            _logger,
            routeId,
            selectedTargetType,
            outcome,
            statusCode,
            summaryServiceId);
    }

    private static Guid? GetServiceId(RouteConfiguration? route, RouteMatch match) =>
        route?.Target is MicroserviceRouteTargetConfiguration microservice
            ? microservice.ServiceId
            : match.Target?.ServiceId;
}
