using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationRatePolicyValidator
{
    internal static void Validate(ClientIpRatePolicyConfiguration? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.TokenLimit <= 0 ||
            value.TokensPerPeriod <= 0 ||
            value.TokensPerPeriod > value.TokenLimit ||
            value.ReplenishmentPeriod <= TimeSpan.Zero ||
            value.ReplenishmentPeriod.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            value.ReplenishmentPeriod > TimeSpan.FromDays(1) ||
            value.QueueLimit < 0 ||
            !Enum.IsDefined(value.RejectionBehavior) ||
            !Enum.IsDefined(value.RetryAfterBehavior))
        {
            HostConfigurationValueValidator.Throw();
        }
    }
}
