using System.Collections.Immutable;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Owns the common real-PostgreSQL scope used by Phase B contract tests.</summary>
internal sealed class PhaseBPostgresContractTestScope : IAsyncDisposable
{
    private readonly NekostickDbContext context;
    private readonly string connectionString;
    private int disposed;

    private PhaseBPostgresContractTestScope(
        PostgresTestDatabase database,
        NekostickDbContext context,
        EfHostConfigApi api,
        string connectionString)
    {
        Database = database;
        this.context = context;
        Api = api;
        this.connectionString = connectionString;
    }

    /// <summary>Gets the isolated PostgreSQL database owned by this test.</summary>
    internal PostgresTestDatabase Database { get; }

    /// <summary>Gets the EF-backed host configuration API for the isolated schema.</summary>
    internal EfHostConfigApi Api { get; }

    /// <summary>Gets the EF context for direct node constraint assertions.</summary>
    internal NekostickDbContext Context => context;

    /// <summary>Creates and migrates an isolated PostgreSQL test scope.</summary>
    internal static async Task<PhaseBPostgresContractTestScope> CreateAsync()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        var database = await PostgresTestDatabase.CreateAsync(connectionString);
        NekostickDbContext? context = null;
        EfHostConfigApi? api = null;
        try
        {
            context = database.CreateContext();
            var result = await database.CreateMigrationCoordinator()
                .MigrateAndValidateAsync(context, TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess, result.Error?.Message);
            api = new EfHostConfigApi(context);
            return new PhaseBPostgresContractTestScope(database, context, api, connectionString);
        }
        catch
        {
            if (api is not null)
            {
                await api.DisposeAsync();
            }

            if (context is not null)
            {
                await context.DisposeAsync();
            }

            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates a PostgreSQL connection using the same external test server.</summary>
    internal NpgsqlConnection CreateConnection() => new(connectionString);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await Api.DisposeAsync();
        await context.DisposeAsync();
        await Database.DisposeAsync();
    }
}

/// <summary>Shared identifiers and configuration builders for Phase B contract tests.</summary>
internal static class PhaseBPostgresContractTestData
{
    internal static readonly Guid ServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000010");

    internal static readonly Guid RouteId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000011");

    internal static readonly Guid MissingServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000012");

    internal const string ExtensionId = "phase-b-extension";

    internal static ConfigurationChangeSet CreateCompleteChangeSet(
        HostConfigurationSnapshot snapshot)
    {
        var service = CreateService(ServiceId, version: 0);
        var route = CreateRoute(ServiceId, version: 0);
        var now = DateTimeOffset.UtcNow;
        var extension = new ExtensionRecordConfiguration(
            ExtensionId,
            "1.2.3",
            ExtensionLoadState.Loaded,
            now,
            now,
            recordVersion: 0);
        var settings = new ExtensionSettingsConfiguration(
            ExtensionId,
            schemaVersion: 2,
            settingsJson: "{\"enabled\":true,\"limit\":3}",
            version: 0);

        return new ConfigurationChangeSet(
            CreateGlobalSettings(snapshot.GlobalSettings.Version, 2048),
            ImmutableArray.Create(route),
            ImmutableArray.Create(service),
            ImmutableArray.Create(extension),
            ImmutableArray.Create(settings));
    }

    internal static ConfigurationChangeSet CreateExtensionChangeSet(
        HostConfigurationSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var extension = new ExtensionRecordConfiguration(
            ExtensionId,
            "1.0.0",
            ExtensionLoadState.Discovered,
            now,
            now,
            recordVersion: 0);
        var settings = new ExtensionSettingsConfiguration(
            ExtensionId,
            schemaVersion: 1,
            settingsJson: "{\"enabled\":true,\"limit\":1}",
            version: 0);

        return new ConfigurationChangeSet(
            CreateGlobalSettings(snapshot.GlobalSettings.Version, 1024),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray.Create(extension),
            ImmutableArray.Create(settings));
    }

    internal static ConfigurationChangeSet CreateGlobalOnlyChangeSet(
        HostConfigurationSnapshot snapshot,
        int maxConcurrentRequests) =>
        new(
            CreateGlobalSettings(snapshot.GlobalSettings.Version, maxConcurrentRequests),
            snapshot.Routes,
            snapshot.Services,
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings);

    internal static ServiceConfiguration CreateService(Guid id, long version) =>
        new(
            id,
            enabled: true,
            fileName: "/usr/bin/phase-b-fixture",
            argumentList: ImmutableArray.Create("--integration"),
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty
                .Add("PHASE_B_MODE", "enabled"),
            startMode: ServiceStartMode.Eager,
            restartPolicy: ServiceRestartPolicy.Always,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: version);

    internal static RouteConfiguration CreateRoute(Guid serviceId, long version) =>
        new(
            RouteId,
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/phase-b",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: new MicroserviceRouteTargetConfiguration(serviceId),
            priority: 10,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, replaceTemplate: null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{}",
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: version);

    internal static GlobalSettingsConfiguration CreateGlobalSettings(
        long version,
        int maxConcurrentRequests) =>
        new(
            version,
            autoPortRangeStart: 21000,
            autoPortRangeEnd: 22000,
            maxRequestBodyBytes: 30 * 1024 * 1024,
            maxConcurrentRequests: maxConcurrentRequests,
            configurationPollInterval: TimeSpan.FromSeconds(30),
            trustedProxyCidrs: ImmutableArray.Create("127.0.0.1/32"));
}
