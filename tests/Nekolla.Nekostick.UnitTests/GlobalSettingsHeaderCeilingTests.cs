using System.Reflection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class GlobalSettingsHeaderCeilingTests
{
    [Fact]
    public void DefaultHeaderLimitDerivesFromThePublishedHardCeiling()
    {
        var settings = new GlobalSettingsConfiguration();
        Assert.Equal(32L * 1024, GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes);
        Assert.Equal(GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes, settings.MaxRequestHeaderBytes);
    }

    [Fact]
    public void DefaultBodyLimitDerivesFromThePublishedHardCeiling()
    {
        var settings = new GlobalSettingsConfiguration();

        Assert.Equal(30L * 1024 * 1024, GlobalSettingsConfiguration.HardMaximumRequestBodyBytes);
        Assert.Equal(GlobalSettingsConfiguration.HardMaximumRequestBodyBytes, settings.MaxRequestBodyBytes);
    }

    [Fact]
    public void ConstructorRejectsHeaderAboveHardCeilingWithHeaderParameter()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GlobalSettingsConfiguration(
                maxRequestHeaderBytes: GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes + 1));

        Assert.Equal("maxRequestHeaderBytes", exception.ParamName);
    }

    [Fact]
    public void ConstructorRejectsBodyAboveHardCeilingWithBodyParameter()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GlobalSettingsConfiguration(
                maxRequestBodyBytes: GlobalSettingsConfiguration.HardMaximumRequestBodyBytes + 1));

        Assert.Equal("maxRequestBodyBytes", exception.ParamName);
    }

    [Fact]
    public void RouteConstructorRejectsInvalidResourceOverrides()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoute(
            maxRequestBodyBytes: GlobalSettingsConfiguration.HardMaximumRequestBodyBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoute(maxRequestBodyBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoute(
            maxRequestHeaderBytes: GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoute(maxRequestHeaderBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoute(maxConcurrentRequests: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoute(requestReadTimeout: TimeSpan.Zero));
    }

    [Fact]
    public void SemanticValidatorRejectsPersistedHeaderAboveHardCeiling()
    {
        var invalidSettings = CreateSettingsWithHeaderLimit(
            GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes + 1);
        var snapshot = new HostConfigurationSnapshot(
            1,
            invalidSettings,
            default,
            default,
            default,
            default);

        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
    }

    [Fact]
    public void SemanticValidatorRejectsPersistedBodyAboveHardCeiling()
    {
        var invalidSettings = CreateSettingsWithBodyLimit(
            GlobalSettingsConfiguration.HardMaximumRequestBodyBytes + 1);
        var snapshot = new HostConfigurationSnapshot(
            1,
            invalidSettings,
            default,
            default,
            default,
            default);

        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
    }

    private static GlobalSettingsConfiguration CreateSettingsWithHeaderLimit(long headerLimit)
    {
        var settings = new GlobalSettingsConfiguration(version: 1);
        var backingField = typeof(GlobalSettingsConfiguration).GetField(
            "<MaxRequestHeaderBytes>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(backingField);
        backingField!.SetValue(settings, headerLimit);
        return settings;
    }

    private static GlobalSettingsConfiguration CreateSettingsWithBodyLimit(long bodyLimit)
    {
        var settings = new GlobalSettingsConfiguration(version: 1);
        var backingField = typeof(GlobalSettingsConfiguration).GetField(
            "<MaxRequestBodyBytes>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(backingField);
        backingField!.SetValue(settings, bodyLimit);
        return settings;
    }

    private static RouteConfiguration CreateRoute(
        long? maxRequestBodyBytes = null,
        long? maxRequestHeaderBytes = null,
        int? maxConcurrentRequests = null,
        TimeSpan? requestReadTimeout = null) =>
        new(
            Guid.Parse("018f3a52-4cde-7abc-8def-0123456789ab"),
            enabled: true,
            matcher: new RouteMatcherConfiguration(RouteMatcherType.Exact, "/resource", default, default),
            target: new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            priority: 0,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
            requestHeaderRewrites: default,
            responseHeaderRewrites: default,
            metadataJson: "{}",
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: 1,
            maxRequestBodyBytes: maxRequestBodyBytes,
            maxRequestHeaderBytes: maxRequestHeaderBytes,
            maxConcurrentRequests: maxConcurrentRequests,
            requestReadTimeout: requestReadTimeout);
}
