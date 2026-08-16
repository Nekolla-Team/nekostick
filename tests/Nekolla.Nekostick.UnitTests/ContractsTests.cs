using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ContractsTests
{
    private static readonly Guid StableId =
        Guid.Parse("018f3a52-4cde-7abc-8def-0123456789ab");

    private static readonly Guid Version4Id =
        Guid.Parse("018f3a52-4cde-4abc-8def-0123456789ab");

    private static readonly Guid InvalidVariantVersion7Id =
        Guid.Parse("018f3a52-4cde-7abc-cdef-0123456789ab");

    [Theory]
    [InlineData(ConfigurationErrorCode.Validation, "Configuration validation failed.")]
    [InlineData(ConfigurationErrorCode.ConcurrencyConflict, "Configuration version conflict.")]
    [InlineData(ConfigurationErrorCode.NotFound, "Configuration item was not found.")]
    [InlineData(ConfigurationErrorCode.Unsupported, "Configuration operation is unsupported.")]
    [InlineData(ConfigurationErrorCode.StorageUnavailable, "Configuration storage is unavailable.")]
    public void ConfigurationErrorsExposeStableSafeMessages(
        ConfigurationErrorCode code,
        string expectedMessage)
    {
        var error = new ConfigurationError(code);

        Assert.Equal(code, error.Code);
        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void UnknownConfigurationErrorCodesUseTheSafeFallbackMessage()
    {
        var error = new ConfigurationError((ConfigurationErrorCode)999);

        Assert.Equal((ConfigurationErrorCode)999, error.Code);
        Assert.Equal("Configuration operation failed.", error.Message);
    }

    [Fact]
    public void ConfigurationReadResultsRepresentSuccessAndFailureImmutably()
    {
        var value = ConfigurationReadResult<string>.Success("safe-value");
        var error = new ConfigurationError(ConfigurationErrorCode.NotFound);
        var failure = ConfigurationReadResult<string>.Failure(error);

        Assert.True(value.IsSuccess);
        Assert.Equal("safe-value", value.Value);
        Assert.Empty(value.Errors);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Single(failure.Errors);
        Assert.Same(error, failure.Errors[0]);
        Assert.Throws<ArgumentException>(
            () => ConfigurationReadResult<string>.Failure());
    }

    [Fact]
    public void ConfigurationWriteResultsExposeOnlyTheRelevantBranch()
    {
        var success = ConfigurationWriteResult.Success(8);
        var error = new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict);
        var failure = ConfigurationWriteResult.Failure(error);

        Assert.True(success.IsSuccess);
        Assert.Equal(8L, success.NewVersion);
        Assert.Empty(success.Errors);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.NewVersion);
        Assert.Single(failure.Errors);
        Assert.Same(error, failure.Errors[0]);
        Assert.Throws<ArgumentException>(() => ConfigurationWriteResult.Failure());
    }

    [Fact]
    public void GlobalSettingsApplyDefaultsAndKeepImmutableProxyValues()
    {
        var defaults = new GlobalSettingsConfiguration();
        var custom = new GlobalSettingsConfiguration(
            version: 3,
            autoPortRangeStart: 21000,
            autoPortRangeEnd: 21010,
            maxRequestBodyBytes: 4096,
            maxConcurrentRequests: 16,
            configurationPollInterval: TimeSpan.FromSeconds(7),
            trustedProxyCidrs: ImmutableArray.Create("192.0.2.0/24"));

        Assert.Equal(0L, defaults.Version);
        Assert.Equal(20000, defaults.AutoPortRangeStart);
        Assert.Equal(29999, defaults.AutoPortRangeEnd);
        Assert.Equal(30L * 1024 * 1024, defaults.MaxRequestBodyBytes);
        Assert.Equal(1024, defaults.MaxConcurrentRequests);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.ConfigurationPollInterval);
        Assert.Empty(defaults.TrustedProxyCidrs);
        Assert.Equal(3L, custom.Version);
        Assert.Equal(21000, custom.AutoPortRangeStart);
        Assert.Equal(21010, custom.AutoPortRangeEnd);
        Assert.Single(custom.TrustedProxyCidrs);
    }

    [Fact]
    public void GlobalSettingsRejectInvalidRangesAndLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GlobalSettingsConfiguration(version: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GlobalSettingsConfiguration(autoPortRangeStart: 300, autoPortRangeEnd: 299));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GlobalSettingsConfiguration(maxRequestBodyBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GlobalSettingsConfiguration(maxConcurrentRequests: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GlobalSettingsConfiguration(configurationPollInterval: TimeSpan.Zero));
    }

    [Fact]
    public void ConfigurationContainersNormalizeDefaultImmutableArrays()
    {
        var globalSettings = new GlobalSettingsConfiguration();
        var changes = new ConfigurationChangeSet(
            globalSettings,
            default,
            default,
            default,
            default);
        var snapshot = new HostConfigurationSnapshot(
            4,
            globalSettings,
            default,
            default,
            default,
            default);

        Assert.Empty(changes.Routes);
        Assert.Empty(changes.Services);
        Assert.Empty(changes.ExtensionRecords);
        Assert.Empty(changes.ExtensionSettings);
        Assert.Empty(snapshot.Routes);
        Assert.Empty(snapshot.Services);
        Assert.Empty(snapshot.ExtensionRecords);
        Assert.Empty(snapshot.ExtensionSettings);
        Assert.Equal(4L, snapshot.Version);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HostConfigurationSnapshot(-1, globalSettings, default, default, default, default));
        Assert.Throws<ArgumentNullException>(
            () => new ConfigurationChangeSet(null!, default, default, default, default));
    }

    [Fact]
    public void HostApiVersionProvidesComparableCompatibilityComponents()
    {
        var current = HostApiVersion.Current;
        var compatibleFeature = new HostApiVersion(current.Major, current.Minor + 1, 0);
        var compatibleFix = new HostApiVersion(current.Major, current.Minor, current.Patch + 1);
        var incompatible = new HostApiVersion(current.Major + 1, 0, 0);

        Assert.Equal(new HostApiVersion(1, 0, 0), current);
        Assert.Equal("1.0.0", current.ToString());
        Assert.True(compatibleFeature.CompareTo(current) > 0);
        Assert.True(compatibleFix.CompareTo(current) > 0);
        Assert.True(incompatible.CompareTo(current) > 0);
        Assert.Equal(0, current.CompareTo(new HostApiVersion(1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostApiVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostApiVersion(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostApiVersion(0, 0, -1));
    }

    [Fact]
    public void HostConfigApiExposesTheStableAsyncContractSurface()
    {
        var apiType = typeof(IHostConfigApi);
        var readSnapshot = apiType.GetMethod(nameof(IHostConfigApi.ReadSnapshotAsync));
        var writeSnapshot = apiType.GetMethod(nameof(IHostConfigApi.WriteSnapshotAsync));
        var readSettings = apiType.GetMethod(nameof(IHostConfigApi.ReadExtensionSettingsAsync));
        var writeSettings = apiType.GetMethod(nameof(IHostConfigApi.WriteExtensionSettingsAsync));

        Assert.NotNull(apiType.GetProperty(nameof(IHostConfigApi.ApiVersion)));
        Assert.NotNull(readSnapshot);
        Assert.NotNull(writeSnapshot);
        Assert.NotNull(readSettings);
        Assert.NotNull(writeSettings);
        Assert.Equal(
            typeof(ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>>),
            readSnapshot!.ReturnType);
        Assert.Equal(typeof(ValueTask<ConfigurationWriteResult>), writeSnapshot!.ReturnType);
        Assert.Equal(
            typeof(ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>>),
            readSettings!.ReturnType);
        Assert.Equal(typeof(ValueTask<ConfigurationWriteResult>), writeSettings!.ReturnType);
    }

    [Fact]
    public void RouteContractsValidateTargetsForwardingAndHeaderRewrites()
    {
        var matcher = new RouteMatcherConfiguration(
            RouteMatcherType.ExactCaseInsensitive,
            "/status",
            default,
            ImmutableArray.Create("GET"));
        var forwarding = new ForwardingConfiguration(ForwardingMode.Preserve, null);
        var replacement = new ForwardingConfiguration(ForwardingMode.Replace, "/v2");
        var header = new HeaderRewriteConfiguration(
            HeaderRewriteOperation.Set,
            "X-Request-Mode",
            "safe");
        var microservice = new MicroserviceRouteTargetConfiguration(StableId);
        var staticTarget = new StaticFileRouteTargetConfiguration(Path.GetTempPath());
        var extensionTarget = new ExtensionHandlerRouteTargetConfiguration("sample.handler");

        Assert.Empty(matcher.HostPatterns);
        Assert.Single(matcher.Methods);
        Assert.Equal(ForwardingMode.Preserve, forwarding.Mode);
        Assert.Null(forwarding.ReplaceTemplate);
        Assert.Equal("/v2", replacement.ReplaceTemplate);
        Assert.Equal(HeaderRewriteOperation.Set, header.Operation);
        Assert.Equal(RouteTargetType.Microservice, microservice.Type);
        Assert.Equal(StableId, microservice.ServiceId);
        Assert.Equal(RouteTargetType.StaticFile, staticTarget.Type);
        Assert.Equal(RouteTargetType.ExtensionHandler, extensionTarget.Type);

        Assert.Throws<ArgumentException>(
            () => new RouteMatcherConfiguration(RouteMatcherType.Exact, " ", default, default));
        Assert.Throws<ArgumentException>(
            () => new HeaderRewriteConfiguration(HeaderRewriteOperation.Remove, " ", null));
        Assert.Throws<ArgumentException>(
            () => new ForwardingConfiguration(ForwardingMode.Replace, null));
        Assert.Throws<ArgumentException>(
            () => new ForwardingConfiguration(ForwardingMode.Strip, "/replacement"));
        Assert.Throws<ArgumentException>(
            () => new MicroserviceRouteTargetConfiguration(Guid.Empty));
        Assert.Throws<ArgumentException>(
            () => new MicroserviceRouteTargetConfiguration(Version4Id));
        Assert.Throws<ArgumentException>(
            () => new MicroserviceRouteTargetConfiguration(InvalidVariantVersion7Id));
        Assert.Throws<ArgumentException>(
            () => new StaticFileRouteTargetConfiguration("relative-root"));
        Assert.Throws<ArgumentException>(
            () => new ExtensionHandlerRouteTargetConfiguration(" "));
    }

    [Fact]
    public void RouteConfigurationNormalizesUtcAndImmutableCollections()
    {
        var createdAt = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.FromHours(5));
        var updatedAt = createdAt.AddMinutes(2);
        var route = new RouteConfiguration(
            StableId,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Prefix, "/api", default, default),
            new ExtensionHandlerRouteTargetConfiguration("sample.handler"),
            2,
            new ForwardingConfiguration(ForwardingMode.Strip, null),
            default,
            default,
            "{}",
            createdAt,
            updatedAt,
            5);

        Assert.Equal(TimeSpan.Zero, route.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, route.UpdatedAt.Offset);
        Assert.Empty(route.RequestHeaderRewrites);
        Assert.Empty(route.ResponseHeaderRewrites);
        Assert.Equal(5L, route.Version);
        Assert.Throws<ArgumentException>(
            () => new RouteConfiguration(
                Guid.Empty,
                true,
                route.Matcher,
                route.Target,
                0,
                route.Forwarding,
                default,
                default,
                "{}",
                createdAt,
                updatedAt,
                0));
        Assert.Throws<ArgumentException>(
            () => new RouteConfiguration(
                Version4Id,
                true,
                route.Matcher,
                route.Target,
                0,
                route.Forwarding,
                default,
                default,
                "{}",
                createdAt,
                updatedAt,
                0));
        Assert.Throws<ArgumentException>(
            () => new RouteConfiguration(
                InvalidVariantVersion7Id,
                true,
                route.Matcher,
                route.Target,
                0,
                route.Forwarding,
                default,
                default,
                "{}",
                createdAt,
                updatedAt,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RouteConfiguration(
                StableId,
                true,
                route.Matcher,
                route.Target,
                0,
                route.Forwarding,
                default,
                default,
                "{}",
                createdAt,
                updatedAt,
                -1));
    }

    [Fact]
    public void ServiceContractsValidateHealthAndNormalizeServiceValues()
    {
        var root = Path.GetTempPath();
        var health = new ServiceHealthCheckConfiguration(
            ServiceHealthCheckType.Http,
            "/health",
            TimeSpan.FromSeconds(4));
        var service = new ServiceConfiguration(
            StableId,
            true,
            Path.Combine(root, "service-bin"),
            default,
            root,
            null!,
            ServiceStartMode.Lazy,
            ServiceRestartPolicy.Always,
            health,
            new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.FromHours(5)),
            new DateTimeOffset(2026, 8, 16, 10, 1, 0, TimeSpan.FromHours(5)),
            2);

        Assert.Empty(service.ArgumentList);
        Assert.Empty(service.Environment);
        Assert.Equal(ServiceStartMode.Lazy, service.StartMode);
        Assert.Equal(ServiceRestartPolicy.Always, service.RestartPolicy);
        Assert.Equal("/health", service.HealthCheck.HttpPath);
        Assert.Equal(TimeSpan.Zero, service.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, service.UpdatedAt.Offset);
        Assert.Equal(2L, service.Version);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.Zero));
        Assert.Throws<ArgumentException>(
            () => new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Http,
                null,
                TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(
            () => new ServiceConfiguration(
                Guid.Empty,
                true,
                Path.Combine(root, "service-bin"),
                default,
                root,
                null!,
                ServiceStartMode.Eager,
                ServiceRestartPolicy.Never,
                health,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                0));
        Assert.Throws<ArgumentException>(
            () => new ServiceConfiguration(
                Version4Id,
                true,
                Path.Combine(root, "service-bin"),
                default,
                root,
                null!,
                ServiceStartMode.Eager,
                ServiceRestartPolicy.Never,
                health,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                0));
        Assert.Throws<ArgumentException>(
            () => new ServiceConfiguration(
                InvalidVariantVersion7Id,
                true,
                Path.Combine(root, "service-bin"),
                default,
                root,
                null!,
                ServiceStartMode.Eager,
                ServiceRestartPolicy.Never,
                health,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                0));
    }

    [Fact]
    public void ExtensionContractsValidateRecordsAndSettings()
    {
        var createdAt = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.FromHours(5));
        var record = new ExtensionRecordConfiguration(
            "sample.extension",
            "1.2.3",
            ExtensionLoadState.Loaded,
            createdAt,
            createdAt.AddMinutes(1),
            3);
        var settings = new ExtensionSettingsConfiguration(
            "sample.extension",
            2,
            "{}",
            4);

        Assert.Equal("sample.extension", record.ExtensionId);
        Assert.Equal("1.2.3", record.Version);
        Assert.Equal(ExtensionLoadState.Loaded, record.LoadState);
        Assert.Equal(TimeSpan.Zero, record.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, record.UpdatedAt.Offset);
        Assert.Equal(3L, record.RecordVersion);
        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal("{}", settings.SettingsJson);
        Assert.Equal(4L, settings.Version);

        Assert.Throws<ArgumentException>(
            () => new ExtensionRecordConfiguration(
                " ", "1.0.0", ExtensionLoadState.Discovered, createdAt, createdAt, 0));
        Assert.Throws<ArgumentException>(
            () => new ExtensionRecordConfiguration(
                "sample.extension", " ", ExtensionLoadState.Discovered, createdAt, createdAt, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExtensionRecordConfiguration(
                "sample.extension", "1.0.0", ExtensionLoadState.Discovered, createdAt, createdAt, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExtensionSettingsConfiguration("sample.extension", -1, "{}", 0));
        Assert.Throws<ArgumentNullException>(
            () => new ExtensionSettingsConfiguration("sample.extension", 0, null!, 0));
    }
}
