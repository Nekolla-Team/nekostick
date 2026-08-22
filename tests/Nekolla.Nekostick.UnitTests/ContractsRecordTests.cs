using Nekolla.Nekostick.Contracts;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ContractsTests
{
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
