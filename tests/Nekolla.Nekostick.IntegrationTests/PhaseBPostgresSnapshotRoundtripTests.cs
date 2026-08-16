using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Xunit;
using static Nekolla.Nekostick.IntegrationTests.PhaseBPostgresContractTestData;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises complete Phase B snapshot round-trips against real PostgreSQL.</summary>
public sealed partial class PhaseBPostgresContractTests
{
    /// <summary>Verifies a complete snapshot round-trips every configuration collection.</summary>
    [Fact]
    public async Task CompleteSnapshotReadAndWriteRoundTripsAllCollections()
    {
        await using var test = await PhaseBPostgresContractTestScope.CreateAsync();
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        Assert.Equal(1L, initial.Value!.Version);
        Assert.Empty(initial.Value!.Routes);
        Assert.Empty(initial.Value!.Services);
        Assert.Empty(initial.Value!.ExtensionRecords);
        Assert.Empty(initial.Value!.ExtensionSettings);

        var changes = CreateCompleteChangeSet(initial.Value!);
        var write = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);

        Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, write.NewVersion);

        var current = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(current.IsSuccess, current.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(current.Value);
        Assert.Equal(2L, current.Value!.Version);
        Assert.Equal(21000, current.Value!.GlobalSettings.AutoPortRangeStart);
        Assert.Equal(22000, current.Value!.GlobalSettings.AutoPortRangeEnd);
        Assert.Equal(
            ImmutableArray.Create("127.0.0.1/32"),
            current.Value!.GlobalSettings.TrustedProxyCidrs);

        var service = Assert.Single(current.Value!.Services);
        Assert.Equal(ServiceId, service.Id);
        Assert.Equal("/usr/bin/phase-b-fixture", service.FileName);
        Assert.Equal("enabled", service.Environment["PHASE_B_MODE"]);
        Assert.Equal(1L, service.Version);

        var route = Assert.Single(current.Value!.Routes);
        Assert.Equal(RouteId, route.Id);
        var routeTarget = Assert.IsType<MicroserviceRouteTargetConfiguration>(route.Target);
        Assert.Equal(ServiceId, routeTarget.ServiceId);
        Assert.Equal(1L, route.Version);

        var extension = Assert.Single(current.Value!.ExtensionRecords);
        Assert.Equal(ExtensionId, extension.ExtensionId);
        Assert.Equal("1.2.3", extension.Version);
        Assert.Equal(1L, extension.RecordVersion);

        var settings = Assert.Single(current.Value!.ExtensionSettings);
        Assert.Equal(ExtensionId, settings.ExtensionId);
        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal(1L, settings.Version);
        using var settingsDocument = JsonDocument.Parse(settings.SettingsJson);
        Assert.True(settingsDocument.RootElement.GetProperty("enabled").GetBoolean());
    }
}
