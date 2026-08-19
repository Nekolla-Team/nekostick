using System.IO;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyRetryPersistenceMapperTests
{
    [Fact]
    public void DefaultPolicyRoundTripsExactly()
    {
        var persisted = ProxyRetryPersistenceMapper.ToPersistence(ProxyRetryConfiguration.Default);
        var restored = ProxyRetryPersistenceMapper.ToContract(
            persisted.MaxRetries,
            persisted.InitialBackoffMilliseconds,
            persisted.MaximumBackoffMilliseconds,
            persisted.RetryOnConnectionFailure,
            persisted.RetryOnUpstreamDisconnect);

        Assert.Equal(ProxyRetryConfiguration.DefaultMaxRetries, persisted.MaxRetries);
        Assert.Equal(200, persisted.InitialBackoffMilliseconds);
        Assert.Equal(2000, persisted.MaximumBackoffMilliseconds);
        Assert.True(persisted.RetryOnConnectionFailure);
        Assert.True(persisted.RetryOnUpstreamDisconnect);
        Assert.Equal(ProxyRetryConfiguration.Default, restored);
    }

    [Fact]
    public void ConfiguredPolicyRoundTripsEveryPersistedValue()
    {
        var configured = new ProxyRetryConfiguration(
            maxRetries: 4,
            initialBackoff: TimeSpan.FromMilliseconds(375),
            maximumBackoff: TimeSpan.FromMilliseconds(1750),
            retryOnConnectionFailure: false,
            retryOnUpstreamDisconnect: true);

        var persisted = ProxyRetryPersistenceMapper.ToPersistence(configured);
        var restored = ProxyRetryPersistenceMapper.ToContract(
            persisted.MaxRetries,
            persisted.InitialBackoffMilliseconds,
            persisted.MaximumBackoffMilliseconds,
            persisted.RetryOnConnectionFailure,
            persisted.RetryOnUpstreamDisconnect);

        Assert.Equal(configured, restored);
    }

    [Fact]
    public void PolicyDefaultsMatchDocumentedSafetyBoundary()
    {
        var policy = ProxyRetryConfiguration.Default;

        Assert.Equal(0, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.InitialBackoff);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.MaximumBackoff);
        Assert.True(policy.RetryOnConnectionFailure);
        Assert.True(policy.RetryOnUpstreamDisconnect);
        Assert.Equal(policy, new GlobalSettingsConfiguration().ProxyRetries);
    }

    [Fact]
    public void PolicyRejectsOutOfBoundsRetriesAndBackoff()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProxyRetryConfiguration(maxRetries: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProxyRetryConfiguration(maxRetries: 11));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProxyRetryConfiguration(initialBackoff: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProxyRetryConfiguration(maximumBackoff: TimeSpan.FromSeconds(3)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProxyRetryConfiguration(
                initialBackoff: TimeSpan.FromSeconds(2),
                maximumBackoff: TimeSpan.FromMilliseconds(1)));
    }
    [Fact]
    public void InvalidPersistedPolicyFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() => ProxyRetryPersistenceMapper.ToContract(
            maxRetries: 11,
            initialBackoffMilliseconds: 200,
            maximumBackoffMilliseconds: 2000,
            retryOnConnectionFailure: true,
            retryOnUpstreamDisconnect: true));
    }
}
