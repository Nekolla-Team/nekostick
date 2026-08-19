using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Xunit;
using static Nekolla.Nekostick.IntegrationTests.PostgresConfigurationContractTestData;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises complete PostgreSQL configuration snapshot round-trips.</summary>
public sealed partial class PostgresConfigurationContractTests
{
    /// <summary>Verifies a complete snapshot round-trips every configuration collection.</summary>
    [Fact]
    public async Task CompleteSnapshotReadAndWriteRoundTripsAllCollections()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
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
        Assert.Equal(32L * 1024, current.Value!.GlobalSettings.MaxRequestHeaderBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), current.Value.GlobalSettings.RequestReadTimeout);
        Assert.Equal(20, current.Value.GlobalSettings.ClientIpRatePolicy!.TokenLimit);
        Assert.Equal(5, current.Value.GlobalSettings.ClientIpRatePolicy.TokensPerPeriod);
        Assert.Equal(TimeSpan.FromSeconds(1), current.Value.GlobalSettings.ClientIpRatePolicy.ReplenishmentPeriod);
        Assert.Equal(RateLimitRejectionBehavior.Queue, current.Value.GlobalSettings.ClientIpRatePolicy.RejectionBehavior);
        Assert.Equal(RateLimitRetryAfterBehavior.FromReplenishmentPeriod, current.Value.GlobalSettings.ClientIpRatePolicy.RetryAfterBehavior);

        var service = Assert.Single(current.Value!.Services);
        Assert.Equal(ServiceId, service.Id);
        Assert.Equal("/usr/bin/fixture-service", service.FileName);
        Assert.Equal("enabled", service.Environment["FIXTURE_MODE"]);
        Assert.Equal(1L, service.Version);

        var route = Assert.Single(current.Value!.Routes);
        Assert.Equal(RouteId, route.Id);
        var routeTarget = Assert.IsType<MicroserviceRouteTargetConfiguration>(route.Target);
        Assert.Equal(ServiceId, routeTarget.ServiceId);
        Assert.Equal(1L, route.Version);
        Assert.Equal(20, route.ClientIpRatePolicy!.TokenLimit);
        Assert.Equal(5, route.ClientIpRatePolicy.TokensPerPeriod);
        Assert.Equal(TimeSpan.FromSeconds(1), route.ClientIpRatePolicy.ReplenishmentPeriod);
        Assert.Equal(2 * 1024 * 1024, route.MaxRequestBodyBytes);
        Assert.Equal(16 * 1024, route.MaxRequestHeaderBytes);
        Assert.Equal(128, route.MaxConcurrentRequests);
        Assert.Equal(TimeSpan.FromSeconds(5), route.RequestReadTimeout);

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

    /// <summary>Verifies PostgreSQL rejects mixed-null global and route rate-policy tuples.</summary>
    [Fact]
    public async Task DatabaseRejectsMixedNullRatePolicyTuples()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        Assert.Null(initial.Value!.GlobalSettings.ClientIpRatePolicy);

        var write = await test.Api.WriteSnapshotAsync(
            initial.Value!.Version,
            CreateCompleteChangeSet(initial.Value!),
            cancellationToken);
        Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);

        var globalException = await Assert.ThrowsAsync<PostgresException>(() =>
            test.Database.ExecuteSchemaCommandAsync(
                $"UPDATE {test.Database.QualifiedRelation("global_settings")} " +
                "SET client_ip_rate_tokens_per_period = NULL;"));
        Assert.Equal("23514", globalException.SqlState);

        var routeException = await Assert.ThrowsAsync<PostgresException>(() =>
            test.Database.ExecuteSchemaCommandAsync(
                $"UPDATE {test.Database.QualifiedRelation("routes")} " +
                "SET client_ip_rate_queue_limit = NULL WHERE id = @id;",
                new NpgsqlParameter("id", RouteId)));
        Assert.Equal("23514", routeException.SqlState);
    }

    /// <summary>Verifies PostgreSQL enforces the global body ceiling and route override bounds.</summary>
    [Fact]
    public async Task DatabaseRejectsOutOfRangeRequestResourceLimits()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var write = await test.Api.WriteSnapshotAsync(
            initial.Value!.Version,
            CreateCompleteChangeSet(initial.Value!),
            cancellationToken);
        Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);

        var globalException = await Assert.ThrowsAsync<PostgresException>(() =>
            test.Database.ExecuteSchemaCommandAsync(
                $"UPDATE {test.Database.QualifiedRelation("global_settings")} " +
                "SET max_request_body_bytes = @value;",
                new NpgsqlParameter(
                    "value",
                    GlobalSettingsConfiguration.HardMaximumRequestBodyBytes + 1)));
        Assert.Equal("23514", globalException.SqlState);

        var invalidRouteValues = new (string Column, object Value)[]
        {
            ("max_request_body_bytes", GlobalSettingsConfiguration.HardMaximumRequestBodyBytes + 1),
            ("max_request_body_bytes", 0),
            ("max_request_header_bytes", GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes + 1),
            ("max_request_header_bytes", 0),
            ("max_concurrent_requests", 0),
            ("request_read_timeout_milliseconds", 0)
        };
        foreach (var (column, value) in invalidRouteValues)
        {
            var routeException = await Assert.ThrowsAsync<PostgresException>(() =>
                test.Database.ExecuteSchemaCommandAsync(
                    $"UPDATE {test.Database.QualifiedRelation("routes")} SET {column} = @value WHERE id = @id;",
                    new NpgsqlParameter("value", value),
                    new NpgsqlParameter("id", RouteId)));

            Assert.Equal("23514", routeException.SqlState);
        }
    }

}
