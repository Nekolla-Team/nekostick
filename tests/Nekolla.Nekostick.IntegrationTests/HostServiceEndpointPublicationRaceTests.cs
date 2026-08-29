using System.Collections.Immutable;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Verifies that database lease republication follows lifecycle readiness.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class HostServiceEndpointPublicationRaceTests
{
    [Fact]
    public async Task PublicationTickDoesNotRepublishWithdrawnLeaseForDeadProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        const int deadPort = 31_947;
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var (serviceId, nodeId, leaseExpiresAt) =
            await SeedLeaseAsync(database, deadPort, cancellationToken);

        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish(
        [
            new HostServiceEndpointLease(serviceId, deadPort, leaseExpiresAt)
        ]);
        publisher.Publish(Array.Empty<HostServiceEndpointLease>());
        Assert.Empty(publisher.Current);

        using var publicationService = new HostServiceEndpointPublicationService(
            new TestDbContextFactory(database),
            new FixedEndpointAuthority(publisher),
            new HostRuntimeOptions(connectionString, nodeId, readOnly: false));
        await InvokePublicationTickAsync(publicationService, cancellationToken);

        var resolver = new HostServiceEndpointResolver(publisher);
        var resolution = await resolver.ResolveAsync(serviceId, cancellationToken);

        Assert.False(
            resolution.IsAvailable,
            "A withdrawn endpoint must remain unavailable when its process is no longer ready.");
        Assert.Empty(publisher.Current);
    }

    [Fact]
    public async Task PublicationTickRepublishesLeaseForActiveReadyEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        const int activePort = 31_948;
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var (serviceId, nodeId, leaseExpiresAt) =
            await SeedLeaseAsync(database, activePort, cancellationToken);

        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish(
        [
            new HostServiceEndpointLease(serviceId, activePort, leaseExpiresAt)
        ]);
        publisher.Publish(Array.Empty<HostServiceEndpointLease>());
        Assert.Empty(publisher.Current);

        using var publicationService = new HostServiceEndpointPublicationService(
            new TestDbContextFactory(database),
            new FixedEndpointAuthority(publisher, (serviceId, activePort)),
            new HostRuntimeOptions(connectionString, nodeId, readOnly: false));
        await InvokePublicationTickAsync(publicationService, cancellationToken);

        var resolver = new HostServiceEndpointResolver(publisher);
        var resolution = await resolver.ResolveAsync(serviceId, cancellationToken);

        Assert.True(resolution.IsAvailable);
        Assert.Equal(activePort, resolution.Endpoint!.BaseUri.Port);
        Assert.True(publisher.Current.ContainsKey(serviceId));
    }

    [Fact]
    public async Task PublicationTickDoesNotPublishLeaseForDifferentAuthorityPort()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        const int databasePort = 31_949;
        const int authorityPort = 31_950;
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var (serviceId, nodeId, _) =
            await SeedLeaseAsync(database, databasePort, cancellationToken);

        var publisher = new HostServiceEndpointSnapshotPublisher();
        using var publicationService = new HostServiceEndpointPublicationService(
            new TestDbContextFactory(database),
            new FixedEndpointAuthority(publisher, (serviceId, authorityPort)),
            new HostRuntimeOptions(connectionString, nodeId, readOnly: false));
        await InvokePublicationTickAsync(publicationService, cancellationToken);

        Assert.Empty(publisher.Current);
        Assert.False(publisher.Current.ContainsKey(serviceId));
    }

    [Fact]
    public async Task PublicationTickLeavesSnapshotUnchangedWhenDatabaseQueryFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        var serviceId = Guid.CreateVersion7();
        const int port = 31_951;
        var initialLease = new HostServiceEndpointLease(
            serviceId,
            port,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var publisher = new HostServiceEndpointSnapshotPublisher();
        publisher.Publish([initialLease]);
        var before = publisher.Current;

        using var publicationService = new HostServiceEndpointPublicationService(
            new ThrowingDbContextFactory(),
            new FixedEndpointAuthority(publisher, (serviceId, port)),
            new HostRuntimeOptions(connectionString, "publication-race-failure", readOnly: false));
        await InvokePublicationTickAsync(publicationService, cancellationToken);

        Assert.Same(before, publisher.Current);
        Assert.Equal(initialLease, publisher.Current[serviceId]);
    }

    private static async Task<(Guid ServiceId, string NodeId, DateTimeOffset LeaseExpiresAt)> SeedLeaseAsync(
        PostgresTestDatabase database,
        int port,
        CancellationToken cancellationToken)
    {
        await using var seedContext = database.CreateContext();
        var migration = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(seedContext, cancellationToken);
        Assert.True(migration.IsSuccess, migration.Error?.Message);

        var serviceId = Guid.CreateVersion7();
        var nodeId = $"publication-race-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddMinutes(5);

        seedContext.Nodes.Add(new Node
        {
            Id = Guid.CreateVersion7(),
            NodeId = nodeId,
            LastHeartbeatAt = now,
            LastConfigurationVersion = 1,
            RuntimeState = "ready",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        });
        seedContext.Services.Add(new Service
        {
            Id = serviceId,
            Enabled = true,
            FileName = "/usr/bin/publication-race-fixture",
            ArgumentListJson = "[]",
            WorkingDirectory = "/tmp",
            EnvironmentJson = "{}",
            StartMode = ServiceStartPolicy.Eager,
            RestartPolicy = ServiceRestartPolicy.OnFailure,
            HealthCheckType = ServiceHealthCheckKind.Process,
            HealthCheckTimeoutMilliseconds = 1_000,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        });
        seedContext.PortLeases.Add(new PortLease
        {
            Id = Guid.CreateVersion7(),
            NodeId = nodeId,
            Port = port,
            ServiceId = serviceId,
            LeaseExpiresAt = leaseExpiresAt,
            RenewedAt = now,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await seedContext.SaveChangesAsync(cancellationToken);
        return (serviceId, nodeId, leaseExpiresAt);
    }

    private static async Task InvokePublicationTickAsync(
        HostServiceEndpointPublicationService publicationService,
        CancellationToken cancellationToken)
    {
        var publishMethod = typeof(HostServiceEndpointPublicationService).GetMethod(
            "PublishAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(publishMethod);

        var invocation = publishMethod!.Invoke(
            publicationService,
            new object[] { cancellationToken });
        var publishTask = Assert.IsAssignableFrom<Task>(invocation);
        await publishTask;
    }

    private sealed class FixedEndpointAuthority : IHostServiceEndpointAuthority
    {
        private readonly HostServiceEndpointSnapshotPublisher _publisher;
        private readonly ImmutableHashSet<(Guid ServiceId, int Port)> _endpoints;

        internal FixedEndpointAuthority(
            HostServiceEndpointSnapshotPublisher publisher,
            params (Guid ServiceId, int Port)[] endpoints)
        {
            _publisher = publisher;
            _endpoints = endpoints.ToImmutableHashSet();
        }

        public Task PublishVerifiedEndpointsAsync(IReadOnlyList<HostServiceEndpointLease> dbLeases)
        {
            _publisher.Publish(
                dbLeases.Where(lease =>
                    lease is not null && _endpoints.Contains((lease.ServiceId, lease.Port))));
            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<NekostickDbContext>
    {
        private readonly PostgresTestDatabase _database;

        internal TestDbContextFactory(PostgresTestDatabase database) => _database = database;

        public NekostickDbContext CreateDbContext() => _database.CreateContext();

        public Task<NekostickDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_database.CreateContext());
        }
    }
    private sealed class ThrowingDbContextFactory : IDbContextFactory<NekostickDbContext>
    {
        public NekostickDbContext CreateDbContext() =>
            throw new InvalidOperationException("Database query failed.");

        public Task<NekostickDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<NekostickDbContext>(new InvalidOperationException("Database query failed."));
    }
}
