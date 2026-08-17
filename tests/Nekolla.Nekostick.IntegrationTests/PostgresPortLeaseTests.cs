using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises durable port lease state transitions against isolated PostgreSQL schemas.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PostgresPortLeaseTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Verifies explicit automatic allocation bounds are honored by persistence.</summary>
    [Fact]
    public async Task AutomaticAcquireStaysWithinRequestBounds()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);

        var result = await test.Store.AcquireAsync(
            PersistencePortLeaseAcquireRequest.Automatic(
                test.NodeId,
                test.ServiceId,
                TimeSpan.FromMinutes(5),
                rangeStart: 25_000,
                rangeEnd: 25_002),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, result.Status);
        Assert.NotNull(result.Lease);
        Assert.InRange(result.Lease!.Port, 25_000, 25_002);
    }

    /// <summary>Verifies a fixed-port conflict does not alter the existing owner's lease.</summary>
    [Fact]
    public async Task FixedPortConflictLeavesOwnerLeaseIntact()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var port = 25_100;
        var owner = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, owner.Status);
        Assert.NotNull(owner.Lease);
        var ownerLease = owner.Lease!;
        var secondServiceId = Guid.CreateVersion7();
        test.Context.Services.Add(CreateService(secondServiceId));
        await test.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var conflict = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                secondServiceId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, conflict.Status);
        var persistedOwner = await test.Context.PortLeases
            .AsNoTracking()
            .SingleAsync(value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
                TestContext.Current.CancellationToken);
        Assert.Equal(ownerLease.Port, persistedOwner.Port);
        Assert.Equal(ownerLease.Version, persistedOwner.Version);
        Assert.Equal(ownerLease.ExpiresAt, persistedOwner.LeaseExpiresAt);
        Assert.Equal(1, await test.Context.PortLeases.CountAsync(
            value => value.NodeId == test.NodeId,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies a deterministic past expiry is reclaimed before a replacement acquire.</summary>
    [Fact]
    public async Task ExpiredLeaseIsReclaimed()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var port = 25_200;
        test.Context.PortLeases.Add(new PortLease
        {
            Id = Guid.CreateVersion7(),
            NodeId = test.NodeId,
            Port = port,
            ServiceId = test.ServiceId,
            LeaseExpiresAt = FixedNow.AddSeconds(-1),
            RenewedAt = FixedNow.AddMinutes(-5),
            Version = 7,
            CreatedAt = FixedNow.AddMinutes(-10),
            UpdatedAt = FixedNow.AddMinutes(-5)
        });
        await test.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var replacement = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, replacement.Status);
        Assert.NotNull(replacement.Lease);
        var replacementLease = replacement.Lease!;
        Assert.Equal(port, replacementLease.Port);
        Assert.Equal(1, replacementLease.Version);
        Assert.True(replacementLease.ExpiresAt > FixedNow);
        Assert.Equal(1, await test.Context.PortLeases.CountAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies wrong and stale renewal versions cannot extend an otherwise valid lease.</summary>
    [Fact]
    public async Task WrongAndStaleRenewalFailWithoutExtendingLease()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var port = 25_300;
        var acquired = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                port,
                TimeSpan.FromMinutes(10)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(acquired.Lease);
        var acquiredLease = acquired.Lease!;

        var wrongVersion = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                port,
                acquiredLease.Version + 1,
                TimeSpan.FromHours(1)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, wrongVersion.Status);
        var unchanged = await ReadLeaseAsync(test, port);
        Assert.Equal(acquiredLease.Version, unchanged.Version);
        Assert.Equal(acquiredLease.ExpiresAt, unchanged.LeaseExpiresAt);

        var renewed = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                port,
                acquiredLease.Version,
                TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, renewed.Status);
        Assert.NotNull(renewed.Lease);
        var renewedLease = renewed.Lease!;

        var stale = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                port,
                acquiredLease.Version,
                TimeSpan.FromHours(1)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, stale.Status);
        var persisted = await ReadLeaseAsync(test, port);
        Assert.Equal(renewedLease.Version, persisted.Version);
        Assert.Equal(renewedLease.ExpiresAt, persisted.LeaseExpiresAt);
    }

    /// <summary>Verifies a versioned release removes the persisted owner lease.</summary>
    [Fact]
    public async Task ReleaseRemovesOwnership()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var port = 25_400;
        var acquired = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(acquired.Lease);
        var lease = acquired.Lease!;

        var released = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                port,
                lease.Version),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, released.Status);
        Assert.Equal(lease.Port, released.Lease!.Port);
        Assert.Equal(0, await test.Context.PortLeases.CountAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies a disposed database context produces a safe unavailable mutation outcome.</summary>
    [Fact]
    public async Task DatabaseOutageReturnsSafeUnavailableOutcome()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        await test.Context.DisposeAsync();

        var result = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                25_500,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.DatabaseUnavailable, result.Status);
        Assert.Null(result.Lease);
    }

    private static async Task<PortLease> ReadLeaseAsync(LeaseTestScope test, int port) =>
        await test.Context.PortLeases.SingleAsync(
            value => value.NodeId == test.NodeId && value.Port == port,
            TestContext.Current.CancellationToken);

    private static Service CreateService(Guid serviceId) =>
        new()
        {
            Id = serviceId,
            Enabled = true,
            FileName = "/usr/bin/lease-fixture",
            ArgumentListJson = "[]",
            WorkingDirectory = "/tmp",
            EnvironmentJson = "{}",
            StartMode = ServiceStartPolicy.Eager,
            RestartPolicy = ServiceRestartPolicy.Never,
            HealthCheckType = ServiceHealthCheckKind.Process,
            HealthCheckTimeoutMilliseconds = 1_000,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
            Version = 1
        };

    private sealed class LeaseTestScope : IAsyncDisposable
    {
        private LeaseTestScope(
            PostgresTestDatabase database,
            NekostickDbContext context,
            EfPortLeaseStore store,
            string nodeId,
            Guid serviceId)
        {
            Database = database;
            Context = context;
            Store = store;
            NodeId = nodeId;
            ServiceId = serviceId;
        }

        internal PostgresTestDatabase Database { get; }
        internal NekostickDbContext Context { get; }
        internal EfPortLeaseStore Store { get; }
        internal string NodeId { get; }
        internal Guid ServiceId { get; }

        internal static async Task<LeaseTestScope> CreateAsync(DateTimeOffset now)
        {
            var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
            var database = await PostgresTestDatabase.CreateAsync(connectionString);
            NekostickDbContext? context = null;
            EfPortLeaseStore? store = null;
            try
            {
                context = database.CreateContext();
                var migration = await database.CreateMigrationCoordinator()
                    .MigrateAndValidateAsync(context, TestContext.Current.CancellationToken);
                Assert.True(migration.IsSuccess, migration.Error?.Message);

                var nodeId = $"port-lease-{Guid.NewGuid():N}";
                var serviceId = Guid.CreateVersion7();
                context.Nodes.Add(new Node
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
                context.Services.Add(CreateService(serviceId));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                store = new EfPortLeaseStore(context, new FixedTimeProvider(now));
                return new LeaseTestScope(database, context, store, nodeId, serviceId);
            }
            catch
            {
                if (store is not null)
                {
                    await store.DisposeAsync();
                }

                if (context is not null)
                {
                    await context.DisposeAsync();
                }

                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        internal FixedTimeProvider(DateTimeOffset now) => this.now = now;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
