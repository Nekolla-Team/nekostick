namespace Nekolla.Nekostick.Persistence;

/// <summary>Defines the PostgreSQL representation and bounds of global proxy timeouts.</summary>
internal static class ProxyTimeoutPersistenceDefaults
{
    internal const int MinimumTimeoutMilliseconds = 1;
    internal const int MaximumTimeoutMilliseconds = 24 * 60 * 60 * 1000;

    internal const int DefaultConnectTimeoutMilliseconds = 10 * 1000;
    internal const int DefaultHttpActivityTimeoutMilliseconds = 30 * 1000;
    internal const int DefaultHttpTotalTimeoutMilliseconds = 100 * 1000;
    internal const int DefaultWebSocketIdleTimeoutMilliseconds = 120 * 1000;

    internal static readonly string CheckConstraintSql =
        $"connect_timeout_milliseconds BETWEEN {MinimumTimeoutMilliseconds} AND {MaximumTimeoutMilliseconds} " +
        $"AND http_activity_timeout_milliseconds BETWEEN {MinimumTimeoutMilliseconds} AND {MaximumTimeoutMilliseconds} " +
        $"AND http_total_timeout_milliseconds BETWEEN {MinimumTimeoutMilliseconds} AND {MaximumTimeoutMilliseconds} " +
        $"AND websocket_idle_timeout_milliseconds BETWEEN {MinimumTimeoutMilliseconds} AND {MaximumTimeoutMilliseconds}";

    internal static bool IsValidTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.FromMilliseconds(MinimumTimeoutMilliseconds) &&
        timeout <= TimeSpan.FromMilliseconds(MaximumTimeoutMilliseconds) &&
        timeout.Ticks % TimeSpan.TicksPerMillisecond == 0;
}
