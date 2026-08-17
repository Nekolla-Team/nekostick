using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
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
}
