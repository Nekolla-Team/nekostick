namespace Nekolla.Nekostick.Contracts;

/// <summary>Defines the immutable global HTTP/1.1 proxy retry policy.</summary>
public sealed record ProxyRetryConfiguration
{
    /// <summary>The documented default number of retries after the first attempt.</summary>
    public const int DefaultMaxRetries = 0;

    /// <summary>The maximum configured retry count.</summary>
    public const int MaximumMaxRetries = 10;

    /// <summary>The documented initial retry backoff.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromMilliseconds(200);

    /// <summary>The documented retry backoff ceiling.</summary>
    public static readonly TimeSpan DefaultMaximumBackoff = TimeSpan.FromSeconds(2);

    /// <summary>Creates validated global proxy retry settings.</summary>
    /// <param name="maxRetries">The number of additional attempts after the first attempt.</param>
    /// <param name="initialBackoff">The initial exponential backoff before jitter.</param>
    /// <param name="maximumBackoff">The maximum exponential backoff before jitter.</param>
    /// <param name="retryOnConnectionFailure">Whether connection failures are retryable.</param>
    /// <param name="retryOnUpstreamDisconnect">Whether pre-response upstream disconnects are retryable.</param>
    public ProxyRetryConfiguration(
        int maxRetries = DefaultMaxRetries,
        TimeSpan? initialBackoff = null,
        TimeSpan? maximumBackoff = null,
        bool retryOnConnectionFailure = true,
        bool retryOnUpstreamDisconnect = true)
    {
        if (maxRetries is < 0 or > MaximumMaxRetries)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries));
        }

        var initial = initialBackoff ?? DefaultInitialBackoff;
        var maximum = maximumBackoff ?? DefaultMaximumBackoff;
        ValidateBackoff(initial, nameof(initialBackoff));
        ValidateBackoff(maximum, nameof(maximumBackoff));
        if (initial > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff));
        }

        MaxRetries = maxRetries;
        InitialBackoff = initial;
        MaximumBackoff = maximum;
        RetryOnConnectionFailure = retryOnConnectionFailure;
        RetryOnUpstreamDisconnect = retryOnUpstreamDisconnect;
    }

    /// <summary>Gets the documented default policy.</summary>
    public static ProxyRetryConfiguration Default { get; } = new();

    /// <summary>Gets the number of additional attempts after the first attempt.</summary>
    public int MaxRetries { get; }

    /// <summary>Gets the initial exponential backoff before jitter.</summary>
    public TimeSpan InitialBackoff { get; }

    /// <summary>Gets the maximum exponential backoff before jitter.</summary>
    public TimeSpan MaximumBackoff { get; }

    /// <summary>Gets whether connection failures are retryable.</summary>
    public bool RetryOnConnectionFailure { get; }

    /// <summary>Gets whether pre-response upstream disconnects are retryable.</summary>
    public bool RetryOnUpstreamDisconnect { get; }

    private static void ValidateBackoff(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero ||
            value > TimeSpan.FromSeconds(2) ||
            value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Retry backoff must be positive, whole milliseconds, and no greater than two seconds.");
        }
    }
}
