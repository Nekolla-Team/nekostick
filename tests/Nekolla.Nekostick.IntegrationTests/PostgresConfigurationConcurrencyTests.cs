using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;
using static Nekolla.Nekostick.IntegrationTests.PostgresConfigurationContractTestData;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises PostgreSQL configuration concurrency, revision, and notification contracts.</summary>
public sealed partial class PostgresConfigurationContractTests
{
    /// <summary>Verifies stale global versions are rejected without changing the committed snapshot.</summary>
    [Fact]
    public async Task SnapshotWriteRejectsOptimisticVersionConflict()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        var changes = CreateGlobalOnlyChangeSet(initial.Value!, 2048);

        var firstWrite = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);
        Assert.True(firstWrite.IsSuccess, firstWrite.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, firstWrite.NewVersion);

        var staleWrite = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);

        Assert.False(staleWrite.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, staleWrite.Errors.Single().Code);
        Assert.Null(staleWrite.NewVersion);

        var current = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(current.IsSuccess, current.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(current.Value);
        Assert.Equal(2L, current.Value!.Version);
        Assert.Equal(2048, current.Value!.GlobalSettings.MaxConcurrentRequests);
    }

    /// <summary>Verifies every committed snapshot mutation advances the global revision exactly once.</summary>
    [Fact]
    public async Task CommittedSnapshotMutationsIncrementTheGlobalRevision()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var database = test.Database;
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var first = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            CreateGlobalOnlyChangeSet(initial.Value, 2048),
            cancellationToken);
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, first.NewVersion);

        var afterFirst = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(afterFirst.IsSuccess, afterFirst.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterFirst.Value);

        var second = await api.WriteSnapshotAsync(
            afterFirst.Value!.Version,
            CreateGlobalOnlyChangeSet(afterFirst.Value, 4096),
            cancellationToken);
        Assert.True(second.IsSuccess, second.Errors.FirstOrDefault()?.Message);
        Assert.Equal(3L, second.NewVersion);

        Assert.Equal(
            3L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("configuration_revisions")} " +
                "WHERE revision_key = @revision_key;",
                new NpgsqlParameter("revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey)));
    }

    /// <summary>Verifies an expiry-boundary lease is removed with its service while unrelated live leases remain.</summary>
    [Fact]
    public async Task ExpiredLeaseAtBoundaryAllowsServiceDeletionAndPreservesUnrelatedLiveLeases()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        await using var api = new EfHostConfigApi(
            test.Context,
            new SnapshotLeaseTimeProvider(SnapshotLeaseCleanupTime));
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await ReadLeaseCleanupSnapshotAsync(api, cancellationToken);
        var removedServiceId = Guid.CreateVersion7();
        var retainedServiceId = Guid.CreateVersion7();
        var addServices = await api.WriteSnapshotAsync(
            initial.Version,
            CreateLeaseServiceAdditionChanges(initial, removedServiceId, retainedServiceId),
            cancellationToken);
        Assert.True(addServices.IsSuccess, addServices.Errors.FirstOrDefault()?.Message);
        var configured = await ReadLeaseCleanupSnapshotAsync(api, cancellationToken);

        var firstNodeId = $"snapshot-lease-{Guid.NewGuid():N}";
        var secondNodeId = $"snapshot-lease-{Guid.NewGuid():N}";
        var expiredLease = CreateSnapshotLease(
            removedServiceId,
            firstNodeId,
            26_100,
            SnapshotLeaseCleanupTime,
            SnapshotLeaseCleanupTime,
            version: 7);
        var firstLiveLease = CreateSnapshotLease(
            retainedServiceId,
            firstNodeId,
            26_101,
            SnapshotLeaseCleanupTime.AddMinutes(10),
            SnapshotLeaseCleanupTime,
            version: 3);
        var secondLiveLease = CreateSnapshotLease(
            retainedServiceId,
            secondNodeId,
            26_102,
            SnapshotLeaseCleanupTime.AddMinutes(10),
            SnapshotLeaseCleanupTime,
            version: 4);
        await AddSnapshotLeasesAsync(
            test.Context,
            SnapshotLeaseCleanupTime,
            cancellationToken,
            expiredLease,
            firstLiveLease,
            secondLiveLease);

        var deletion = await api.WriteSnapshotAsync(
            configured.Version,
            CreateLeaseServiceRemovalChanges(configured, removedServiceId),
            cancellationToken);

        Assert.True(deletion.IsSuccess, deletion.Errors.FirstOrDefault()?.Message);
        Assert.Equal(configured.Version + 1, deletion.NewVersion);
        await using var verificationContext = test.Database.CreateContext();
        var remainingLeases = await verificationContext.PortLeases
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        Assert.Equal(2, remainingLeases.Count);
        Assert.DoesNotContain(remainingLeases, value => value.Id == expiredLease.Id);
        var persistedFirstLiveLease = remainingLeases.Single(value => value.Id == firstLiveLease.Id);
        Assert.Equal(firstLiveLease.NodeId, persistedFirstLiveLease.NodeId);
        Assert.Equal(firstLiveLease.ServiceId, persistedFirstLiveLease.ServiceId);
        Assert.Equal(firstLiveLease.GenerationId, persistedFirstLiveLease.GenerationId);
        Assert.Equal(firstLiveLease.LeaseExpiresAt, persistedFirstLiveLease.LeaseExpiresAt);
        Assert.Equal(firstLiveLease.Version, persistedFirstLiveLease.Version);
        var persistedSecondLiveLease = remainingLeases.Single(value => value.Id == secondLiveLease.Id);
        Assert.Equal(secondLiveLease.NodeId, persistedSecondLiveLease.NodeId);
        Assert.Equal(secondLiveLease.ServiceId, persistedSecondLiveLease.ServiceId);
        Assert.Equal(secondLiveLease.GenerationId, persistedSecondLiveLease.GenerationId);
        Assert.Equal(secondLiveLease.LeaseExpiresAt, persistedSecondLiveLease.LeaseExpiresAt);
        Assert.Equal(secondLiveLease.Version, persistedSecondLiveLease.Version);
        Assert.False(await verificationContext.Services.AsNoTracking().AnyAsync(
            value => value.Id == removedServiceId,
            cancellationToken));
        Assert.True(await verificationContext.Services.AsNoTracking().AnyAsync(
            value => value.Id == retainedServiceId,
            cancellationToken));
    }

    /// <summary>Verifies a live lease rejects service deletion while the expired lease remains persisted.</summary>
    [Fact]
    public async Task LiveLeaseBlocksServiceDeletionWithoutCleaningExpiredLeases()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        await using var api = new EfHostConfigApi(
            test.Context,
            new SnapshotLeaseTimeProvider(SnapshotLeaseCleanupTime));
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await ReadLeaseCleanupSnapshotAsync(api, cancellationToken);
        var serviceId = Guid.CreateVersion7();
        var addService = await api.WriteSnapshotAsync(
            initial.Version,
            CreateLeaseServiceAdditionChanges(initial, serviceId),
            cancellationToken);
        Assert.True(addService.IsSuccess, addService.Errors.FirstOrDefault()?.Message);
        var configured = await ReadLeaseCleanupSnapshotAsync(api, cancellationToken);

        var expiredLease = CreateSnapshotLease(
            serviceId,
            $"snapshot-lease-{Guid.NewGuid():N}",
            26_200,
            SnapshotLeaseCleanupTime,
            SnapshotLeaseCleanupTime,
            version: 7);
        var liveLease = CreateSnapshotLease(
            serviceId,
            $"snapshot-lease-{Guid.NewGuid():N}",
            26_201,
            SnapshotLeaseCleanupTime.AddMinutes(5),
            SnapshotLeaseCleanupTime,
            version: 9);
        await AddSnapshotLeasesAsync(
            test.Context,
            SnapshotLeaseCleanupTime,
            cancellationToken,
            expiredLease,
            liveLease);

        var rejected = await api.WriteSnapshotAsync(
            configured.Version,
            CreateLeaseServiceRemovalChanges(configured, serviceId),
            cancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, rejected.Errors.Single().Code);
        Assert.Null(rejected.NewVersion);
        await using var verificationContext = test.Database.CreateContext();
        var persistedLeases = await verificationContext.PortLeases
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        Assert.Equal(2, persistedLeases.Count);
        var persistedExpiredLease = persistedLeases.Single(value => value.Id == expiredLease.Id);
        Assert.Equal(expiredLease.GenerationId, persistedExpiredLease.GenerationId);
        Assert.Equal(expiredLease.LeaseExpiresAt, persistedExpiredLease.LeaseExpiresAt);
        Assert.Equal(expiredLease.Version, persistedExpiredLease.Version);
        var persistedLiveLease = persistedLeases.Single(value => value.Id == liveLease.Id);
        Assert.Equal(liveLease.GenerationId, persistedLiveLease.GenerationId);
        Assert.Equal(liveLease.LeaseExpiresAt, persistedLiveLease.LeaseExpiresAt);
        Assert.Equal(liveLease.Version, persistedLiveLease.Version);
        Assert.True(await verificationContext.Services.AsNoTracking().AnyAsync(
            value => value.Id == serviceId,
            cancellationToken));
        var revision = await verificationContext.ConfigurationRevisions.AsNoTracking().SingleAsync(
            value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
            cancellationToken);
        Assert.Equal(configured.Version, revision.Version);
    }

    /// <summary>Verifies stale global revisions cannot commit expired-lease cleanup.</summary>
    [Fact]
    public async Task RevisionConflictDoesNotCommitExpiredLeaseCleanup()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        await using var api = new EfHostConfigApi(
            test.Context,
            new SnapshotLeaseTimeProvider(SnapshotLeaseCleanupTime));
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await ReadLeaseCleanupSnapshotAsync(api, cancellationToken);
        var serviceId = Guid.CreateVersion7();
        var addService = await api.WriteSnapshotAsync(
            initial.Version,
            CreateLeaseServiceAdditionChanges(initial, serviceId),
            cancellationToken);
        Assert.True(addService.IsSuccess, addService.Errors.FirstOrDefault()?.Message);
        var configured = await ReadLeaseCleanupSnapshotAsync(api, cancellationToken);
        var expiredLease = CreateSnapshotLease(
            serviceId,
            $"snapshot-lease-{Guid.NewGuid():N}",
            26_300,
            SnapshotLeaseCleanupTime,
            SnapshotLeaseCleanupTime,
            version: 11);
        await AddSnapshotLeasesAsync(
            test.Context,
            SnapshotLeaseCleanupTime,
            cancellationToken,
            expiredLease);

        var conflicted = await api.WriteSnapshotAsync(
            configured.Version - 1,
            CreateLeaseServiceRemovalChanges(configured, serviceId),
            cancellationToken);

        Assert.False(conflicted.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, conflicted.Errors.Single().Code);
        Assert.Null(conflicted.NewVersion);
        await using var verificationContext = test.Database.CreateContext();
        var persistedLease = await verificationContext.PortLeases.AsNoTracking().SingleAsync(
            value => value.Id == expiredLease.Id,
            cancellationToken);
        Assert.Equal(expiredLease.GenerationId, persistedLease.GenerationId);
        Assert.Equal(expiredLease.LeaseExpiresAt, persistedLease.LeaseExpiresAt);
        Assert.Equal(expiredLease.Version, persistedLease.Version);
        Assert.True(await verificationContext.Services.AsNoTracking().AnyAsync(
            value => value.Id == serviceId,
            cancellationToken));
        var revision = await verificationContext.ConfigurationRevisions.AsNoTracking().SingleAsync(
            value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
            cancellationToken);
        Assert.Equal(configured.Version, revision.Version);
    }

    /// <summary>Verifies the PostgreSQL notification carries the committed revision on the contract channel.</summary>
    [Fact]
    public async Task SnapshotWritePublishesCommittedRevisionNotification()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var api = test.Api;
        await using var listener = test.CreateConnection();
        var cancellationToken = TestContext.Current.CancellationToken;
        await listener.OpenAsync(cancellationToken);
        await using (var listenCommand = new NpgsqlCommand(
                         "LISTEN nekostick_config_changed;",
                         listener))
        {
            await listenCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var notification = new TaskCompletionSource<NpgsqlNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNotification(object? _, NpgsqlNotificationEventArgs args) =>
            notification.TrySetResult(args);

        listener.Notification += OnNotification;
        using var listenerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listenerCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var waitTask = listener.WaitAsync(listenerCancellation.Token);
        try
        {
            var initial = await api.ReadSnapshotAsync(cancellationToken);
            Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(initial.Value);

            var write = await api.WriteSnapshotAsync(
                initial.Value!.Version,
                CreateGlobalOnlyChangeSet(initial.Value, 2048),
                cancellationToken);
            Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
            Assert.Equal(2L, write.NewVersion);

            var received = await notification.Task.WaitAsync(listenerCancellation.Token);
            Assert.Equal("nekostick_config_changed", received.Channel);
            Assert.Equal("2", received.Payload);
            await waitTask;
        }
        finally
        {
            listenerCancellation.Cancel();
            try
            {
                await waitTask;
            }
            catch (OperationCanceledException)
            {
                // The bounded listener is intentionally canceled during cleanup.
            }

            listener.Notification -= OnNotification;
        }
    }

    private static readonly DateTimeOffset SnapshotLeaseCleanupTime =
        new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static async Task<HostConfigurationSnapshot> ReadLeaseCleanupSnapshotAsync(
        EfHostConfigApi api,
        CancellationToken cancellationToken)
    {
        var result = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private static ConfigurationChangeSet CreateLeaseServiceAdditionChanges(
        HostConfigurationSnapshot snapshot,
        params Guid[] serviceIds) =>
        new(
            snapshot.GlobalSettings,
            snapshot.Routes,
            snapshot.Services
                .Concat(serviceIds.Select(serviceId => CreateService(serviceId, version: 0)))
                .ToImmutableArray(),
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings);

    private static ConfigurationChangeSet CreateLeaseServiceRemovalChanges(
        HostConfigurationSnapshot snapshot,
        Guid serviceId) =>
        new(
            snapshot.GlobalSettings,
            snapshot.Routes
                .Where(value => value.Target is not MicroserviceRouteTargetConfiguration target ||
                    target.ServiceId != serviceId)
                .ToImmutableArray(),
            snapshot.Services
                .Where(value => value.Id != serviceId)
                .ToImmutableArray(),
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings);

    private static PortLease CreateSnapshotLease(
        Guid serviceId,
        string nodeId,
        int port,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        long version) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            NodeId = nodeId,
            Port = port,
            ServiceId = serviceId,
            GenerationId = Guid.CreateVersion7(),
            LeaseExpiresAt = expiresAt,
            RenewedAt = now.AddMinutes(-1),
            Version = version,
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };

    private static async Task AddSnapshotLeasesAsync(
        NekostickDbContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        params PortLease[] leases)
    {
        context.Nodes.AddRange(
            leases.Select(value => value.NodeId)
                .Distinct(StringComparer.Ordinal)
                .Select(nodeId => new Node
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
                }));
        context.PortLeases.AddRange(leases);
        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed class SnapshotLeaseTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        internal SnapshotLeaseTimeProvider(DateTimeOffset now) => this.now = now;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
