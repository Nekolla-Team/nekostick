using System.IO;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;
/// <summary>Maps nullable rate-policy columns to and from immutable contract values.</summary>
public static class RatePolicyPersistenceMapper
{
    /// <summary>Maps persisted nullable policy columns to an immutable contract policy.</summary>
    /// <exception cref="InvalidDataException">The persisted columns are incomplete or semantically invalid.</exception>
    public static ClientIpRatePolicyConfiguration? ToContract(
        long? tokenLimit,
        long? tokensPerPeriod,
        int? replenishmentPeriodMilliseconds,
        int? queueLimit,
        RateLimitRejectionBehavior? rejectionBehavior,
        RateLimitRetryAfterBehavior? retryAfterBehavior)
    {
        var allNull = tokenLimit is null &&
            tokensPerPeriod is null &&
            replenishmentPeriodMilliseconds is null &&
            queueLimit is null &&
            rejectionBehavior is null &&
            retryAfterBehavior is null;
        if (allNull)
        {
            return null;
        }

        if (tokenLimit is null || tokensPerPeriod is null || replenishmentPeriodMilliseconds is null ||
            queueLimit is null || rejectionBehavior is null || retryAfterBehavior is null)
        {
            throw new InvalidDataException("A persisted rate policy is incomplete.");
        }

        try
        {
            return new ClientIpRatePolicyConfiguration(
                tokenLimit.Value,
                tokensPerPeriod.Value,
                TimeSpan.FromMilliseconds(replenishmentPeriodMilliseconds.Value),
                queueLimit.Value,
                rejectionBehavior.Value,
                retryAfterBehavior.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("A persisted rate policy is invalid.");
        }
    }

    internal static (long? TokenLimit,
        long? TokensPerPeriod,
        int? ReplenishmentPeriodMilliseconds,
        int? QueueLimit,
        RateLimitRejectionBehavior? RejectionBehavior,
        RateLimitRetryAfterBehavior? RetryAfterBehavior) ToPersistence(
        ClientIpRatePolicyConfiguration? value)
    {
        if (value is null)
        {
            return (null, null, null, null, null, null);
        }

        return (
            value.TokenLimit,
            value.TokensPerPeriod,
            checked((int)value.ReplenishmentPeriod.TotalMilliseconds),
            value.QueueLimit,
            value.RejectionBehavior,
            value.RetryAfterBehavior);
    }
}
