using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Contracts;
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
                test.GenerationId,
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
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, owner.Status);
        Assert.NotNull(owner.Lease);
        var ownerLease = owner.Lease!;
        var secondServiceId = Guid.CreateVersion7();
        var secondGenerationId = Guid.CreateVersion7();
        test.Context.Services.Add(CreateService(secondServiceId));
        await test.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var conflict = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                secondServiceId,
                secondGenerationId,
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
            GenerationId = test.GenerationId,
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
                test.GenerationId,
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

    /// <summary>Verifies acquire and renewal snapshots match PostgreSQL microsecond timestamp precision.</summary>
    [Fact]
    public async Task AcquireAndRenewSnapshotsMatchPersistedTimestampPrecision()
    {
        var now = FixedNow.AddTicks(9);
        await using var test = await LeaseTestScope.CreateAsync(now);
        var cancellationToken = TestContext.Current.CancellationToken;

        var acquired = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                25_250,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, acquired.Status);
        Assert.NotNull(acquired.Lease);
        var acquiredLease = acquired.Lease!;

        await using var verificationContext = test.Database.CreateContext();
        var acquiredEntity = await verificationContext.PortLeases.AsNoTracking()
            .SingleAsync(
                value => value.NodeId == test.NodeId && value.Port == 25_250,
                cancellationToken);
        Assert.Equal(FixedNow, acquiredEntity.CreatedAt);
        Assert.Equal(FixedNow.AddMinutes(5), acquiredLease.ExpiresAt);
        Assert.Equal(acquiredLease.AcquiredAt, acquiredEntity.CreatedAt);
        Assert.Equal(acquiredLease.ExpiresAt, acquiredEntity.LeaseExpiresAt);
        Assert.Equal(acquiredEntity.CreatedAt, acquiredEntity.RenewedAt);
        Assert.Equal(acquiredEntity.CreatedAt, acquiredEntity.UpdatedAt);

        var renewedAt = FixedNow.AddMinutes(1).AddTicks(9);
        test.SetTime(renewedAt);

        var renewed = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                25_250,
                acquiredLease.Version,
                TimeSpan.FromMinutes(10)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, renewed.Status);
        Assert.NotNull(renewed.Lease);
        var renewedLease = renewed.Lease!;

        var renewedEntity = await verificationContext.PortLeases.AsNoTracking()
            .SingleAsync(
                value => value.NodeId == test.NodeId && value.Port == 25_250,
                cancellationToken);
        Assert.Equal(FixedNow.AddMinutes(1), renewedEntity.RenewedAt);
        Assert.Equal(FixedNow.AddMinutes(1), renewedEntity.UpdatedAt);
        Assert.Equal(renewedLease.AcquiredAt, renewedEntity.CreatedAt);
        Assert.Equal(renewedLease.ExpiresAt, renewedEntity.LeaseExpiresAt);
        Assert.Equal(FixedNow.AddMinutes(11), renewedLease.ExpiresAt);
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
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(10)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(acquired.Lease);
        var acquiredLease = acquired.Lease!;

        var wrongVersion = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
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
                test.GenerationId,
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
                test.GenerationId,
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
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(acquired.Lease);
        var lease = acquired.Lease!;

        var released = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                lease.Version),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, released.Status);
        Assert.Equal(lease.Port, released.Lease!.Port);
        Assert.Equal(0, await test.Context.PortLeases.CountAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies disabled services can release leases without weakening acquire, renew, or version checks.</summary>
    [Fact]
    public async Task DisabledServiceCanReleaseLeaseWithVersionChecks()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        const int port = 25_600;
        var acquired = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, acquired.Status);
        Assert.NotNull(acquired.Lease);
        var lease = acquired.Lease!;

        var service = await test.Context.Services.SingleAsync(
            value => value.Id == test.ServiceId,
            cancellationToken);
        service.Enabled = false;
        await test.Context.SaveChangesAsync(cancellationToken);

        var acquire = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port + 1,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Rejected, acquire.Status);

        var renew = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                lease.Version,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Rejected, renew.Status);

        var wrongVersion = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                lease.Version + 1),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, wrongVersion.Status);

        await using var verificationContext = test.Database.CreateContext();
        var unchanged = await verificationContext.PortLeases.AsNoTracking().SingleAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
            cancellationToken);
        Assert.Equal(lease.Version, unchanged.Version);
        Assert.Equal(lease.ExpiresAt, unchanged.LeaseExpiresAt);

        var released = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                lease.Version),
            cancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, released.Status);
        Assert.Equal(lease.Port, released.Lease!.Port);
        Assert.Equal(0, await verificationContext.PortLeases.CountAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
            cancellationToken));
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
                test.GenerationId,
                25_500,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.DatabaseUnavailable, result.Status);
        Assert.Null(result.Lease);
    }

    /// <summary>Verifies a generation cannot reacquire live ownership and another generation gets a distinct port.</summary>
    [Fact]
    public async Task GenerationsAcquireDistinctPortsAndSameGenerationCannotReacquire()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        const int firstPort = 25_700;
        const int secondPort = 25_701;

        var first = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                firstPort,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, first.Status);
        var firstLease = first.Lease!;

        var sameGeneration = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                secondPort,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, sameGeneration.Status);

        var secondGenerationId = Guid.CreateVersion7();
        var second = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                secondGenerationId,
                secondPort,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, second.Status);
        var secondLease = second.Lease!;

        Assert.NotEqual(firstLease.GenerationId, secondLease.GenerationId);
        Assert.NotEqual(firstLease.Port, secondLease.Port);
        var persisted = await test.Context.PortLeases.AsNoTracking()
            .Where(value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, value => value.GenerationId == firstLease.GenerationId && value.Port == firstPort);
        Assert.Contains(persisted, value => value.GenerationId == secondGenerationId && value.Port == secondPort);
    }

    /// <summary>Verifies concurrent generations acquire separate ports from the same range.</summary>
    [Fact]
    public async Task ConcurrentGenerationAcquiresUseDistinctPorts()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstGenerationId = Guid.CreateVersion7();
        var secondGenerationId = Guid.CreateVersion7();

        var firstTask = test.Store.AcquireAsync(
            PersistencePortLeaseAcquireRequest.Automatic(
                test.NodeId,
                test.ServiceId,
                firstGenerationId,
                TimeSpan.FromMinutes(5),
                rangeStart: 25_710,
                rangeEnd: 25_711),
            cancellationToken).AsTask();
        var secondTask = test.Store.AcquireAsync(
            PersistencePortLeaseAcquireRequest.Automatic(
                test.NodeId,
                test.ServiceId,
                secondGenerationId,
                TimeSpan.FromMinutes(5),
                rangeStart: 25_710,
                rangeEnd: 25_711),
            cancellationToken).AsTask();

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.All(results, result => Assert.Equal(PersistencePortLeaseOperationStatus.Applied, result.Status));
        Assert.NotNull(results[0].Lease);
        Assert.NotNull(results[1].Lease);
        var firstLease = results[0].Lease!;
        var secondLease = results[1].Lease!;
        Assert.Equal(firstGenerationId, firstLease.GenerationId);
        Assert.Equal(secondGenerationId, secondLease.GenerationId);
        Assert.NotEqual(firstLease.Port, secondLease.Port);
    }

    /// <summary>Verifies wrong-generation and stale lease keys cannot mutate another generation.</summary>
    [Fact]
    public async Task WrongGenerationAndStaleVersionCannotRenewOrReleaseOtherLeases()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        const int firstPort = 25_800;
        const int secondPort = 25_801;
        var secondGenerationId = Guid.CreateVersion7();

        var first = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                firstPort,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, first.Status);
        var firstLease = first.Lease!;

        var second = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                secondGenerationId,
                secondPort,
                TimeSpan.FromMinutes(7)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, second.Status);
        var secondLease = second.Lease!;

        var wrongGeneration = secondGenerationId;
        var wrongRenewal = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                wrongGeneration,
                firstPort,
                firstLease.Version,
                TimeSpan.FromHours(1)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.NotFound, wrongRenewal.Status);

        var wrongRelease = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                wrongGeneration,
                firstPort,
                firstLease.Version),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.NotFound, wrongRelease.Status);

        var staleRenewal = await test.Store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                firstLease.GenerationId,
                firstPort,
                firstLease.Version + 1,
                TimeSpan.FromHours(1)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, staleRenewal.Status);

        var staleRelease = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                firstLease.GenerationId,
                firstPort,
                firstLease.Version + 1),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, staleRelease.Status);

        await using var verificationContext = test.Database.CreateContext();
        var persistedFirst = await verificationContext.PortLeases.AsNoTracking().SingleAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId && value.GenerationId == firstLease.GenerationId,
            cancellationToken);
        var persistedSecond = await verificationContext.PortLeases.AsNoTracking().SingleAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId && value.GenerationId == secondGenerationId,
            cancellationToken);
        Assert.Equal(firstLease.Version, persistedFirst.Version);
        Assert.Equal(firstLease.ExpiresAt, persistedFirst.LeaseExpiresAt);
        Assert.Equal(secondLease.Version, persistedSecond.Version);
        Assert.Equal(secondLease.ExpiresAt, persistedSecond.LeaseExpiresAt);
        Assert.Equal(firstPort, persistedFirst.Port);
        Assert.Equal(secondPort, persistedSecond.Port);
        Assert.Equal(2, await verificationContext.PortLeases.CountAsync(
            value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId,
            cancellationToken));

        var released = await test.Store.ReleaseAsync(
            new PersistencePortLeaseReleaseRequest(
                test.NodeId,
                test.ServiceId,
                firstLease.GenerationId,
                firstPort,
                firstLease.Version),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, released.Status);

        var remaining = await verificationContext.PortLeases.AsNoTracking().ToListAsync(cancellationToken);
        var preservedGeneration = Assert.Single(remaining);
        Assert.Equal(secondGenerationId, preservedGeneration.GenerationId);
        Assert.Equal(secondPort, preservedGeneration.Port);
        Assert.Equal(secondLease.Version, preservedGeneration.Version);
        Assert.Equal(secondLease.ExpiresAt, preservedGeneration.LeaseExpiresAt);
    }

    /// <summary>Verifies any live generation lease prevents its service from being removed.</summary>
    [Fact]
    public async Task LiveLeasesAcrossGenerationsBlockServiceRemoval()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                25_900,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, first.Status);

        var secondGenerationId = Guid.CreateVersion7();
        var second = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                secondGenerationId,
                25_901,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, second.Status);

        await using var api = new EfHostConfigApi(test.Context, new FixedTimeProvider(FixedNow));
        var snapshotRead = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(snapshotRead.IsSuccess, snapshotRead.Errors.FirstOrDefault()?.Message);
        var snapshot = snapshotRead.Value!;
        var changes = new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            snapshot.Routes,
            snapshot.Services.Where(value => value.Id != test.ServiceId).ToImmutableArray(),
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings);

        var removal = await api.WriteSnapshotAsync(snapshot.Version, changes, cancellationToken);
        Assert.False(removal.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, removal.Errors.Single().Code);

        await using var verificationContext = test.Database.CreateContext();
        var leases = await verificationContext.PortLeases.AsNoTracking()
            .Where(value => value.NodeId == test.NodeId && value.ServiceId == test.ServiceId)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, leases.Count);
        Assert.Contains(leases, value => value.GenerationId == test.GenerationId);
        Assert.Contains(leases, value => value.GenerationId == secondGenerationId);
        Assert.True(await verificationContext.Services.AsNoTracking().AnyAsync(
            value => value.Id == test.ServiceId,
            cancellationToken));
    }

    /// <summary>Verifies non-UUIDv7 generation identifiers are rejected before persistence.</summary>
    [Fact]
    public async Task NonUuidV7GenerationIdentifierIsRejected()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var result = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                Guid.NewGuid(),
                25_950,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Rejected, result.Status);
        Assert.Empty(await test.Context.PortLeases.ToListAsync(TestContext.Current.CancellationToken));
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
            RestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy.Never,
            HealthCheckType = ServiceHealthCheckKind.Process,
            HealthCheckTimeoutMilliseconds = 1_000,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
            Version = 1
        };

    private sealed class LeaseTestScope : IAsyncDisposable
    {
        private readonly FixedTimeProvider _timeProvider;

        private LeaseTestScope(
            PostgresTestDatabase database,
            NekostickDbContext context,
            EfPortLeaseStore store,
            FixedTimeProvider timeProvider,
            string nodeId,
            Guid serviceId,
            Guid generationId)
        {
            Database = database;
            Context = context;
            Store = store;
            _timeProvider = timeProvider;
            GenerationId = generationId;
            NodeId = nodeId;
            ServiceId = serviceId;
        }

        internal PostgresTestDatabase Database { get; }
        internal NekostickDbContext Context { get; }
        internal EfPortLeaseStore Store { get; }
        internal string NodeId { get; }
        internal Guid ServiceId { get; }
        internal Guid GenerationId { get; }

        internal void SetTime(DateTimeOffset now) => _timeProvider.SetUtcNow(now);

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
                var generationId = Guid.CreateVersion7();
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
                var timeProvider = new FixedTimeProvider(now);
                store = new EfPortLeaseStore(context, timeProvider);
                return new LeaseTestScope(database, context, store, timeProvider, nodeId, serviceId, generationId);
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
        private DateTimeOffset now;

        internal FixedTimeProvider(DateTimeOffset now) => this.now = now;

        internal void SetUtcNow(DateTimeOffset value) => now = value;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
