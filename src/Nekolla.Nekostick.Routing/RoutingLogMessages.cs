using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Routing;

internal static partial class RoutingLogMessages
{
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Debug,
        Message = "Route host value validation was rejected. Operation: {Operation}.")]
    internal static partial void HostValueValidationRejected(
        ILogger logger,
        string operation);
}
