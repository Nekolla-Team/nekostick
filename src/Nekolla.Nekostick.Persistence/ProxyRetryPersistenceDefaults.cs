using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Defines the PostgreSQL representation and bounds of global proxy retries.</summary>
internal static class ProxyRetryPersistenceDefaults
{
    internal const int MinimumBackoffMilliseconds = 1;
    internal const int MaximumBackoffMilliseconds = 2 * 1000;
    internal const int DefaultMaxRetries = ProxyRetryConfiguration.DefaultMaxRetries;
    internal const int DefaultInitialBackoffMilliseconds = 200;
    internal const int DefaultMaximumRetryBackoffMilliseconds = MaximumBackoffMilliseconds;

    internal const bool DefaultRetryOnConnectionFailure = true;
    internal const bool DefaultRetryOnUpstreamDisconnect = true;

    internal static readonly string CheckConstraintSql =
        $"proxy_max_retries BETWEEN 0 AND {ProxyRetryConfiguration.MaximumMaxRetries} " +
        $"AND proxy_initial_retry_backoff_milliseconds BETWEEN {MinimumBackoffMilliseconds} AND {MaximumBackoffMilliseconds} " +
        $"AND proxy_maximum_retry_backoff_milliseconds BETWEEN {MinimumBackoffMilliseconds} AND {MaximumBackoffMilliseconds} " +
        "AND proxy_initial_retry_backoff_milliseconds <= proxy_maximum_retry_backoff_milliseconds";

    internal static bool IsValidRetryPolicy(ProxyRetryConfiguration policy) =>
        policy.MaxRetries is >= 0 and <= ProxyRetryConfiguration.MaximumMaxRetries &&
        IsValidBackoff(policy.InitialBackoff) &&
        IsValidBackoff(policy.MaximumBackoff) &&
        policy.InitialBackoff <= policy.MaximumBackoff;

    private static bool IsValidBackoff(TimeSpan value) =>
        value >= TimeSpan.FromMilliseconds(MinimumBackoffMilliseconds) &&
        value <= TimeSpan.FromMilliseconds(MaximumBackoffMilliseconds) &&
        value.Ticks % TimeSpan.TicksPerMillisecond == 0;
}
