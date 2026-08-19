using System.IO;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class RatePolicyPersistenceMapperTests
{
    private static readonly (
        long? TokenLimit,
        long? TokensPerPeriod,
        int? ReplenishmentPeriodMilliseconds,
        int? QueueLimit,
        RateLimitRejectionBehavior? RejectionBehavior,
        RateLimitRetryAfterBehavior? RetryAfterBehavior)[] MixedNullPolicies =
    [
        (null, 5, 1000, 0, RateLimitRejectionBehavior.Reject, RateLimitRetryAfterBehavior.None),
        (10, null, 1000, 0, RateLimitRejectionBehavior.Reject, RateLimitRetryAfterBehavior.None),
        (10, 5, null, 0, RateLimitRejectionBehavior.Reject, RateLimitRetryAfterBehavior.None),
        (10, 5, 1000, null, RateLimitRejectionBehavior.Reject, RateLimitRetryAfterBehavior.None),
        (10, 5, 1000, 0, null, RateLimitRetryAfterBehavior.None),
        (10, 5, 1000, 0, RateLimitRejectionBehavior.Reject, null)
    ];

    [Fact]
    public void AllNullPersistedTupleMeansUnlimitedGlobalPolicy()
    {
        var policy = MapNullPolicy();
        var globalSettings = new GlobalSettingsConfiguration(clientIpRatePolicy: policy);

        Assert.Null(globalSettings.ClientIpRatePolicy);
    }

    [Fact]
    public void AllNullPersistedTupleMeansInheritedRoutePolicy()
    {
        var route = new RouteConfiguration(
            Guid.Parse("018f3a52-4cde-7abc-8def-0123456789ab"),
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/", default, default),
            new ExtensionHandlerRouteTargetConfiguration("handler"),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            default,
            default,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            clientIpRatePolicy: MapNullPolicy());

        Assert.Null(route.ClientIpRatePolicy);
        Assert.Null(route.MaxRequestBodyBytes);
        Assert.Null(route.MaxRequestHeaderBytes);
        Assert.Null(route.MaxConcurrentRequests);
        Assert.Null(route.RequestReadTimeout);
    }

    [Fact]
    public void EveryMixedNullTupleFailsClosed()
    {
        foreach (var policy in MixedNullPolicies)
        {
            Assert.Throws<InvalidDataException>(() => RatePolicyPersistenceMapper.ToContract(
                policy.TokenLimit,
                policy.TokensPerPeriod,
                policy.ReplenishmentPeriodMilliseconds,
                policy.QueueLimit,
                policy.RejectionBehavior,
                policy.RetryAfterBehavior));
        }
    }

    private static ClientIpRatePolicyConfiguration? MapNullPolicy() =>
        RatePolicyPersistenceMapper.ToContract(null, null, null, null, null, null);
}
