namespace Nekolla.Nekostick.Contracts;

/// <summary>Specifies how a client-IP rate-limited request is handled when no token is available.</summary>
public enum RateLimitRejectionBehavior
{
    /// <summary>Reject the request immediately when it cannot be admitted or queued.</summary>
    Reject,

    /// <summary>Queue the request up to the configured queue limit before rejecting it.</summary>
    Queue
}

/// <summary>Specifies whether a rejected rate-limited request receives a Retry-After value.</summary>
public enum RateLimitRetryAfterBehavior
{
    /// <summary>Do not emit a Retry-After value.</summary>
    None,

    /// <summary>Emit a Retry-After value derived from the replenishment period.</summary>
    FromReplenishmentPeriod,

}
/// <summary>Defines immutable token-bucket settings for one client-IP rate policy.</summary>
public sealed record ClientIpRatePolicyConfiguration
{
    /// <summary>Creates a validated client-IP token-bucket policy.</summary>
    /// <param name="tokenLimit">The maximum number of tokens held by a bucket.</param>
    /// <param name="tokensPerPeriod">The number of tokens replenished each period.</param>
    /// <param name="replenishmentPeriod">The positive, whole-millisecond replenishment period.</param>
    /// <param name="queueLimit">The maximum number of requests waiting for a token.</param>
    /// <param name="rejectionBehavior">The behavior when admission cannot proceed.</param>
    /// <param name="retryAfterBehavior">The behavior for the Retry-After response value.</param>
    public ClientIpRatePolicyConfiguration(
        long tokenLimit,
        long tokensPerPeriod,
        TimeSpan replenishmentPeriod,
        int queueLimit,
        RateLimitRejectionBehavior rejectionBehavior,
        RateLimitRetryAfterBehavior retryAfterBehavior)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tokenLimit, 0);

        if (tokensPerPeriod <= 0 || tokensPerPeriod > tokenLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(tokensPerPeriod));
        }

        if (replenishmentPeriod <= TimeSpan.Zero ||
            replenishmentPeriod.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            replenishmentPeriod > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(replenishmentPeriod));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(queueLimit);

        if (!Enum.IsDefined(rejectionBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(rejectionBehavior));
        }

        if (!Enum.IsDefined(retryAfterBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterBehavior));
        }

        TokenLimit = tokenLimit;
        TokensPerPeriod = tokensPerPeriod;
        ReplenishmentPeriod = replenishmentPeriod;
        QueueLimit = queueLimit;
        RejectionBehavior = rejectionBehavior;
        RetryAfterBehavior = retryAfterBehavior;
    }

    /// <summary>Gets the maximum number of tokens held by a bucket.</summary>
    public long TokenLimit { get; }

    /// <summary>Gets the number of tokens replenished each period.</summary>
    public long TokensPerPeriod { get; }

    /// <summary>Gets the replenishment period.</summary>
    public TimeSpan ReplenishmentPeriod { get; }

    /// <summary>Gets the maximum number of queued requests.</summary>
    public int QueueLimit { get; }

    /// <summary>Gets the admission rejection behavior.</summary>
    public RateLimitRejectionBehavior RejectionBehavior { get; }

    /// <summary>Gets the Retry-After response behavior.</summary>
    public RateLimitRetryAfterBehavior RetryAfterBehavior { get; }
}
