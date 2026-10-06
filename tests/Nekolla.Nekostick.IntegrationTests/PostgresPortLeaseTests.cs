using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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

    /// <summary>Verifies bare and wrapped commit-time transient failures retry complete mutations.</summary>
    [Theory]
    [InlineData("Acquire", "40001", false)]
    [InlineData("Renew", "40P01", true)]
    [InlineData("Release", "40001", true)]
    public async Task TransientCommitFailuresRetryWholeMutation(
        string operation,
        string sqlState,
        bool wrapped)
    {
        const int port = 25_960;
        var interceptor = wrapped ? new WrappingPostgresTransactionFailureInterceptor() : null;
        await using var test = await LeaseTestScope.CreateAsync(FixedNow, interceptor);
        var cancellationToken = TestContext.Current.CancellationToken;
        PersistencePortLease? originalLease = null;

        if (operation is "Renew" or "Release")
        {
            var acquired = await test.Store.AcquireAsync(
                new PersistencePortLeaseAcquireRequest(
                    test.NodeId,
                    test.ServiceId,
                    test.GenerationId,
                    port,
                    TimeSpan.FromMinutes(5)),
                cancellationToken);
            Assert.Equal(PersistencePortLeaseOperationStatus.Applied, acquired.Status);
            originalLease = Assert.IsType<PersistencePortLease>(acquired.Lease);
        }

        await InstallTransientCommitFailureAsync(test.Database, port, failedAttempts: 1, sqlState);
        var result = operation switch
        {
            "Acquire" => await test.Store.AcquireAsync(
                new PersistencePortLeaseAcquireRequest(
                    test.NodeId,
                    test.ServiceId,
                    test.GenerationId,
                    port,
                    TimeSpan.FromMinutes(5)),
                cancellationToken),
            "Renew" => await test.Store.RenewAsync(
                new PersistencePortLeaseRenewRequest(
                    test.NodeId,
                    test.ServiceId,
                    test.GenerationId,
                    port,
                    originalLease!.Version,
                    TimeSpan.FromMinutes(5)),
                cancellationToken),
            "Release" => await test.Store.ReleaseAsync(
                new PersistencePortLeaseReleaseRequest(
                    test.NodeId,
                    test.ServiceId,
                    test.GenerationId,
                    port,
                    originalLease!.Version),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, result.Status);
        if (wrapped)
        {
            Assert.Equal(1, interceptor!.WrappedFailureCount);
        }

        Assert.Equal(2L, await ReadTransientCommitAttemptsAsync(test.Database));
        await using var verificationContext = test.Database.CreateContext();
        var persisted = await verificationContext.PortLeases.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.NodeId == test.NodeId && value.Port == port,
                cancellationToken);
        if (operation == "Release")
        {
            Assert.Null(persisted);
        }
        else
        {
            Assert.NotNull(persisted);
            Assert.Equal(operation == "Renew" ? originalLease!.Version + 1 : 1, persisted.Version);
        }
    }

    /// <summary>Verifies three failed whole-operation attempts return a transient result.</summary>
    [Fact]
    public async Task ExhaustedTransientCommitFailuresReturnRetryableTransient()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        const int port = 25_961;
        var cancellationToken = TestContext.Current.CancellationToken;
        await InstallTransientCommitFailureAsync(
            test.Database,
            port,
            failedAttempts: 3,
            PostgresErrorCodes.SerializationFailure);

        var result = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            cancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.RetryableTransient, result.Status);
        Assert.Null(result.Lease);
        Assert.Equal(3L, await ReadTransientCommitAttemptsAsync(test.Database));
        await using var verificationContext = test.Database.CreateContext();
        Assert.Empty(await verificationContext.PortLeases.AsNoTracking()
            .Where(value => value.NodeId == test.NodeId && value.Port == port)
            .ToListAsync(cancellationToken));
    }

    /// <summary>Verifies cancellation after a transient commit failure is not reported as an outage.</summary>
    [Fact]
    public async Task CancellationDuringTransientRetryReturnsCancelled()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var interceptor = new CancelAfterTransientPostgresFailureInterceptor(cancellation);
        await using var test = await LeaseTestScope.CreateAsync(FixedNow, interceptor);
        const int port = 25_962;
        await InstallTransientCommitFailureAsync(
            test.Database,
            port,
            failedAttempts: 3,
            PostgresErrorCodes.SerializationFailure);

        var result = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            cancellation.Token);

        Assert.Equal(PersistencePortLeaseOperationStatus.Cancelled, result.Status);
        Assert.Equal(1L, await ReadTransientCommitAttemptsAsync(test.Database));
        await using var verificationContext = test.Database.CreateContext();
        Assert.Empty(await verificationContext.PortLeases.AsNoTracking()
            .Where(value => value.NodeId == test.NodeId && value.Port == port)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies a retried active snapshot reads the new repeatable-read view.</summary>
    [Fact]
    public async Task ReadActiveRetriesWithFreshSnapshotAfterTransientCommitFailure()
    {
        var interceptor = new FreshSnapshotCommitFailureInterceptor();
        await using var test = await LeaseTestScope.CreateAsync(FixedNow, interceptor);
        const int port = 25_963;
        var generationId = Guid.CreateVersion7();
        interceptor.Arm(test.Database, test.NodeId, test.ServiceId, generationId, port);

        var result = await test.Store.ReadActiveAsync(
            test.NodeId,
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseSnapshotStatus.Available, result.Status);
        var lease = Assert.Single(result.Leases);
        Assert.Equal(port, lease.Port);
        Assert.Equal(generationId, lease.GenerationId);
        Assert.Equal(1, interceptor.FailureCount);
    }

    /// <summary>Verifies a real PostgreSQL unique violation remains a single-attempt conflict.</summary>
    [Fact]
    public async Task UniqueViolationRemainsConflictWithoutRetry()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        const int port = 25_964;
        var cancellationToken = TestContext.Current.CancellationToken;
        await InstallUniqueViolationTriggerAsync(test.Database);

        var result = await test.Store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            cancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.Conflict, result.Status);
        Assert.Null(result.Lease);
        Assert.Equal(1L, await ReadUniqueViolationAttemptsAsync(test.Database));
        await using var verificationContext = test.Database.CreateContext();
        Assert.Empty(await verificationContext.PortLeases.AsNoTracking()
            .Where(value => value.NodeId == test.NodeId && value.Port == port)
            .ToListAsync(cancellationToken));
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

    /// <summary>Verifies a non-transient acquire failure on a shared external context leaves no tracked lease.</summary>
    [Fact]
    public async Task NonTransientAcquireFailureDetachesTrackedLeasesOnInjectedContext()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        Assert.DoesNotContain(test.Context.ChangeTracker.Entries<PortLease>(), entry => true);

        const int port = 26_100;
        var interceptor = new SingleSaveChangesFailureInterceptor();
        interceptor.Arm();
        await using var sharedContext = test.CreateFreshExternalContext(interceptor);
        await using var store = new EfPortLeaseStore(sharedContext, test.TimeProvider);

        var failed = await store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.DatabaseUnavailable, failed.Status);
        Assert.Null(failed.Lease);
        Assert.Equal(1, interceptor.FailureCount);
        Assert.DoesNotContain(sharedContext.ChangeTracker.Entries<PortLease>(), entry => true);
        Assert.Empty(await sharedContext.PortLeases.AsNoTracking().ToListAsync(
            TestContext.Current.CancellationToken));

        var flushed = await sharedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, flushed);

        var retry = await store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, retry.Status);
        Assert.NotNull(retry.Lease);
        Assert.Equal(0, sharedContext.ChangeTracker.Entries<PortLease>()
            .Count(entry => entry.State != EntityState.Unchanged));
    }

    /// <summary>Verifies a non-transient renewal failure leaves no stale tracker state on a shared external context.</summary>
    [Fact]
    public async Task NonTransientRenewFailureDetachesTrackedLeasesOnInjectedContext()
    {
        await using var test = await LeaseTestScope.CreateAsync(FixedNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        const int port = 26_101;
        var interceptor = new SingleSaveChangesFailureInterceptor();
        await using var sharedContext = test.CreateFreshExternalContext(interceptor);
        await using var store = new EfPortLeaseStore(sharedContext, test.TimeProvider);

        var acquired = await store.AcquireAsync(
            new PersistencePortLeaseAcquireRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, acquired.Status);
        var lease = Assert.IsType<PersistencePortLease>(acquired.Lease);

        interceptor.Arm();
        var failed = await store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                lease.Version,
                TimeSpan.FromMinutes(10)),
            cancellationToken);

        Assert.Equal(PersistencePortLeaseOperationStatus.DatabaseUnavailable, failed.Status);
        Assert.Null(failed.Lease);
        Assert.Equal(1, interceptor.FailureCount);
        Assert.DoesNotContain(sharedContext.ChangeTracker.Entries<PortLease>(), entry => true);

        var unchanged = await sharedContext.PortLeases.AsNoTracking().SingleAsync(
            value => value.NodeId == test.NodeId && value.Port == port,
            cancellationToken);
        Assert.Equal(lease.Version, unchanged.Version);

        var flushed = await sharedContext.SaveChangesAsync(cancellationToken);
        Assert.Equal(0, flushed);

        var renewal = await store.RenewAsync(
            new PersistencePortLeaseRenewRequest(
                test.NodeId,
                test.ServiceId,
                test.GenerationId,
                port,
                lease.Version,
                TimeSpan.FromMinutes(10)),
            cancellationToken);
        Assert.Equal(PersistencePortLeaseOperationStatus.Applied, renewal.Status);
        Assert.NotNull(renewal.Lease);
        Assert.Equal(lease.Version + 1, renewal.Lease!.Version);
        Assert.Equal(0, sharedContext.ChangeTracker.Entries<PortLease>()
            .Count(entry => entry.State != EntityState.Unchanged));
    }

    private static async Task InstallTransientCommitFailureAsync(
        PostgresTestDatabase database,
        int port,
        int failedAttempts,
        string sqlState)
    {
        var errorCode = sqlState switch
        {
            PostgresErrorCodes.SerializationFailure => PostgresErrorCodes.SerializationFailure,
            PostgresErrorCodes.DeadlockDetected => PostgresErrorCodes.DeadlockDetected,
            _ => throw new ArgumentOutOfRangeException(nameof(sqlState))
        };
        var sequence = database.QualifiedRelation("port_lease_transient_attempts");
        var function = database.QualifiedRelation("raise_port_lease_transient_failure");
        var table = database.QualifiedRelation("port_leases");

        await database.ExecuteSchemaCommandAsync($"CREATE SEQUENCE {sequence} START WITH 1;");
        await database.ExecuteSchemaCommandAsync(
            $"""
            CREATE FUNCTION {function}() RETURNS trigger
            LANGUAGE plpgsql
            AS $lease$
            DECLARE
                affected_port integer;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    affected_port := OLD.port;
                ELSE
                    affected_port := NEW.port;
                END IF;

                IF affected_port = {port} AND
                   nextval('{sequence}'::regclass) <= {failedAttempts} THEN
                    RAISE EXCEPTION 'Injected test transient lease failure.'
                        USING ERRCODE = '{errorCode}';
                END IF;

                RETURN NULL;
            END;
            $lease$;
            """);
        await database.ExecuteSchemaCommandAsync(
            $"CREATE CONSTRAINT TRIGGER port_lease_transient_failure " +
            $"AFTER INSERT OR UPDATE OR DELETE ON {table} " +
            "DEFERRABLE INITIALLY DEFERRED FOR EACH ROW " +
            $"EXECUTE FUNCTION {function}();");
    }

    private static async Task InstallUniqueViolationTriggerAsync(PostgresTestDatabase database)
    {
        var sequence = database.QualifiedRelation("port_lease_unique_attempts");
        var function = database.QualifiedRelation("cause_port_lease_unique_violation");
        var table = database.QualifiedRelation("port_leases");

        await database.ExecuteSchemaCommandAsync($"CREATE SEQUENCE {sequence} START WITH 1;");
        await database.ExecuteSchemaCommandAsync(
            $"""
            CREATE FUNCTION {function}() RETURNS trigger
            LANGUAGE plpgsql
            AS $lease$
            BEGIN
                IF pg_trigger_depth() = 1 THEN
                    PERFORM nextval('{sequence}'::regclass);
                    INSERT INTO {table} (
                        id,
                        node_id,
                        port,
                        service_id,
                        generation_id,
                        lease_expires_at,
                        renewed_at,
                        version,
                        created_at,
                        updated_at)
                    VALUES (
                        NEW.id,
                        NEW.node_id,
                        NEW.port,
                        NEW.service_id,
                        NEW.generation_id,
                        NEW.lease_expires_at,
                        NEW.renewed_at,
                        NEW.version,
                        NEW.created_at,
                        NEW.updated_at);
                END IF;

                RETURN NEW;
            END;
            $lease$;
            """);
        await database.ExecuteSchemaCommandAsync(
            $"CREATE TRIGGER port_lease_duplicate_before_insert " +
            $"BEFORE INSERT ON {table} FOR EACH ROW " +
            $"EXECUTE FUNCTION {function}();");
    }

    private static async Task<long> ReadTransientCommitAttemptsAsync(PostgresTestDatabase database)
    {
        var sequence = database.QualifiedRelation("port_lease_transient_attempts");
        return await database.ExecuteScalarAsync<long>($"SELECT last_value FROM {sequence};");
    }

    private static async Task<long> ReadUniqueViolationAttemptsAsync(PostgresTestDatabase database)
    {
        var sequence = database.QualifiedRelation("port_lease_unique_attempts");
        return await database.ExecuteScalarAsync<long>($"SELECT last_value FROM {sequence};");
    }

    private static async Task RaisePostgresSerializationFailureAsync(PostgresTestDatabase database)
    {
        try
        {
            await database.ExecuteSchemaCommandAsync(
                "DO $lease$ BEGIN " +
                "RAISE EXCEPTION 'Injected test serialization failure.' " +
                "USING ERRCODE = '40001'; END; $lease$;");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw;
        }

        throw new InvalidOperationException("PostgreSQL did not raise the test serialization failure.");
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }

    private sealed class WrappingPostgresTransactionFailureInterceptor : DbTransactionInterceptor
    {
        private int _wrappedFailureCount;

        internal int WrappedFailureCount => Volatile.Read(ref _wrappedFailureCount);

        public override Task TransactionFailedAsync(
            DbTransaction transaction,
            TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (FindPostgresException(eventData.Exception)?.SqlState is
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                Interlocked.Increment(ref _wrappedFailureCount);
                throw new InvalidOperationException(
                    "The test wrapped the PostgreSQL transaction failure.",
                    eventData.Exception);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CancelAfterTransientPostgresFailureInterceptor : DbTransactionInterceptor
    {
        private readonly CancellationTokenSource _cancellation;
        private int _cancelled;

        internal CancelAfterTransientPostgresFailureInterceptor(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public override Task TransactionFailedAsync(
            DbTransaction transaction,
            TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (FindPostgresException(eventData.Exception)?.SqlState is
                    (PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected) &&
                Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                _cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FreshSnapshotCommitFailureInterceptor : DbTransactionInterceptor
    {
        private PostgresTestDatabase? _database;
        private string? _nodeId;
        private Guid _serviceId;
        private Guid _generationId;
        private int _port;
        private int _armed;
        private int _failed;
        private int _failureCount;

        internal int FailureCount => Volatile.Read(ref _failureCount);

        internal void Arm(
            PostgresTestDatabase database,
            string nodeId,
            Guid serviceId,
            Guid generationId,
            int port)
        {
            _database = database;
            _nodeId = nodeId;
            _serviceId = serviceId;
            _generationId = generationId;
            _port = port;
            Volatile.Write(ref _armed, 1);
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0 || Interlocked.CompareExchange(ref _failed, 1, 0) != 0)
            {
                return result;
            }

            Interlocked.Increment(ref _failureCount);
            var database = _database ?? throw new InvalidOperationException("The test database was not armed.");
            await using (var context = database.CreateContext())
            {
                context.PortLeases.Add(new PortLease
                {
                    Id = Guid.CreateVersion7(),
                    NodeId = _nodeId!,
                    Port = _port,
                    ServiceId = _serviceId,
                    GenerationId = _generationId,
                    LeaseExpiresAt = FixedNow.AddMinutes(5),
                    RenewedAt = FixedNow,
                    Version = 1,
                    CreatedAt = FixedNow,
                    UpdatedAt = FixedNow
                });
                await context.SaveChangesAsync(cancellationToken);
            }

            await RaisePostgresSerializationFailureAsync(database);
            return result;
        }
    }

    private sealed class SingleSaveChangesFailureInterceptor : SaveChangesInterceptor
    {
        private int _armed;
        private int _fired;
        private int _failureCount;

        internal int FailureCount => Volatile.Read(ref _failureCount);

        /// <summary>Arms the next SaveChanges call to fail once with a non-SQLSTATE database error.</summary>
        internal void Arm() => Volatile.Write(ref _armed, 1);

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            FailOnce();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailOnce();
            return ValueTask.FromResult(result);
        }

        private void FailOnce()
        {
            if (Volatile.Read(ref _armed) == 0 || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _failureCount);
            throw new InvalidOperationException(
                "The test injected a non-transient lease persistence failure.");
        }
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
        internal TimeProvider TimeProvider => _timeProvider;

        internal void SetTime(DateTimeOffset now) => _timeProvider.SetUtcNow(now);

        /// <summary>Creates an independent external context sharing the store's database.</summary>
        /// <param name="interceptors">Optional EF interceptors for the external context.</param>
        /// <returns>A fresh unshared EF context on the owned schema.</returns>
        internal NekostickDbContext CreateFreshExternalContext(params IInterceptor[] interceptors) =>
            Database.CreateContext(interceptors);

        internal static async Task<LeaseTestScope> CreateAsync(
            DateTimeOffset now,
            IInterceptor? interceptor = null)
        {
            var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
            var database = await PostgresTestDatabase.CreateAsync(connectionString);
            NekostickDbContext? context = null;
            EfPortLeaseStore? store = null;
            try
            {
                context = database.CreateContext(interceptor);
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
