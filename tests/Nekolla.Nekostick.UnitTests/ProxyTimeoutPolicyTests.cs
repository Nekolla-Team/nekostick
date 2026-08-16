using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyTimeoutPolicyTests
{
    [Fact]
    public void ProxyTimeoutConfigurationUsesTheDocumentedDefaultsInOrder()
    {
        var configuration = ProxyTimeoutConfiguration.Default;

        Assert.Equal(TimeSpan.FromSeconds(10), configuration.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.HttpActivityTimeout);
        Assert.Equal(TimeSpan.FromSeconds(100), configuration.HttpTotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.WebSocketIdleTimeout);
        Assert.True(configuration.ConnectTimeout < configuration.HttpActivityTimeout);
        Assert.True(configuration.HttpActivityTimeout < configuration.HttpTotalTimeout);
        Assert.True(configuration.HttpTotalTimeout < configuration.WebSocketIdleTimeout);
    }

    [Fact]
    public void ProxyTimeoutConfigurationAcceptsItsInclusiveMillisecondBounds()
    {
        var minimum = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromMilliseconds(1),
            httpActivityTimeout: TimeSpan.FromMilliseconds(1),
            httpTotalTimeout: TimeSpan.FromMilliseconds(1),
            webSocketIdleTimeout: TimeSpan.FromMilliseconds(1));
        var maximum = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromDays(1),
            httpActivityTimeout: TimeSpan.FromDays(1),
            httpTotalTimeout: TimeSpan.FromDays(1),
            webSocketIdleTimeout: TimeSpan.FromDays(1));

        Assert.Equal(TimeSpan.FromMilliseconds(1), minimum.ConnectTimeout);
        Assert.Equal(TimeSpan.FromDays(1), maximum.WebSocketIdleTimeout);
    }

    [Fact]
    public void ProxyTimeoutConfigurationRejectsInvalidBoundsAndPrecision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProxyTimeoutConfiguration(connectTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProxyTimeoutConfiguration(httpActivityTimeout: TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProxyTimeoutConfiguration(
                httpTotalTimeout: TimeSpan.FromDays(1).Add(TimeSpan.FromMilliseconds(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProxyTimeoutConfiguration(webSocketIdleTimeout: TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void MicroserviceTimeoutPolicyPreservesEachNamedTimeoutWithoutSwappingOrder()
    {
        var policy = new MicroserviceTimeoutPolicy(
            connectTimeout: TimeSpan.FromSeconds(2),
            activityTimeout: TimeSpan.FromSeconds(5),
            httpTotalTimeout: TimeSpan.FromSeconds(11),
            websocketIdleTimeout: TimeSpan.FromSeconds(17));

        Assert.Equal(TimeSpan.FromSeconds(2), policy.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.ActivityTimeout);
        Assert.Equal(TimeSpan.FromSeconds(11), policy.HttpTotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(17), policy.WebSocketIdleTimeout);
    }

    [Fact]
    public void MicroserviceTimeoutPolicyAcceptsItsInclusiveUpperBound()
    {
        var maximum = TimeSpan.FromMilliseconds(int.MaxValue);
        var policy = new MicroserviceTimeoutPolicy(maximum, maximum, maximum, maximum);

        Assert.Equal(maximum, policy.ConnectTimeout);
        Assert.Equal(maximum, policy.ActivityTimeout);
        Assert.Equal(maximum, policy.HttpTotalTimeout);
        Assert.Equal(maximum, policy.WebSocketIdleTimeout);
    }

    [Fact]
    public void MicroserviceTimeoutPolicyRejectsNonPositiveAndOverBudgetValues()
    {
        var valid = TimeSpan.FromSeconds(1);
        var overBudget = TimeSpan.FromMilliseconds(int.MaxValue).Add(TimeSpan.FromMilliseconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MicroserviceTimeoutPolicy(TimeSpan.Zero, valid, valid, valid));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MicroserviceTimeoutPolicy(valid, TimeSpan.FromMilliseconds(-1), valid, valid));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MicroserviceTimeoutPolicy(valid, valid, overBudget, valid));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MicroserviceTimeoutPolicy(valid, valid, valid, overBudget));
    }
}
