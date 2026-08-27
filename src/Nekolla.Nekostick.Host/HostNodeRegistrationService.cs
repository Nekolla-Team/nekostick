using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Host;

/// <summary>Registers this process and maintains its persisted node heartbeat.</summary>
public sealed class HostNodeRegistrationService : BackgroundService
{
    private readonly IDbContextFactory<NekostickDbContext> _dbContextFactory;
    private readonly IHostConfigurationSnapshotAccessor _snapshotAccessor;
    private readonly HostRuntimeState _runtimeState;
    private readonly HostRuntimeOptions _options;
    private readonly IHostNodeActivityLease _activityLease;
    private readonly ILogger<HostNodeRegistrationService> _logger;
    private NekostickDbContext? _dbContext;

    /// <summary>Creates the node registration and heartbeat service.</summary>
    public HostNodeRegistrationService(
        IDbContextFactory<NekostickDbContext> dbContextFactory,
        IHostConfigurationSnapshotAccessor snapshotAccessor,
        HostRuntimeState runtimeState,
        HostRuntimeOptions options,
        ILogger<HostNodeRegistrationService> logger,
        IHostNodeActivityLease? activityLease = null)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _snapshotAccessor = snapshotAccessor ?? throw new ArgumentNullException(nameof(snapshotAccessor));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _activityLease = activityLease ?? new PostgresHostNodeActivityLease(options);
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await _dbContext.Database.OpenConnectionAsync(cancellationToken);
            await _activityLease.AcquireAsync(
                _dbContext.Database.GetDbConnection(),
                cancellationToken);
            await base.StartAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(StartAsync));
            await DisposeResourcesAsync();
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RegisterOrHeartbeatAsync(stoppingToken);
                    await Task.Delay(_options.HeartbeatInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HostNodeActivityLostException exception)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(RegisterOrHeartbeatAsync));
                    _runtimeState.MarkDatabaseUnavailable();
                    HostLogMessages.NodeHeartbeatUnavailable(_logger);
                    return;
                }
                catch (Exception exception)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(RegisterOrHeartbeatAsync));
                    _runtimeState.MarkDatabaseUnavailable();
                    _dbContext?.ChangeTracker.Clear();
                    HostLogMessages.NodeHeartbeatUnavailable(_logger);
                    await Task.Delay(
                        HostRetryPolicy.GetDelay(
                            _options.ReconnectInitialDelay,
                            _options.ReconnectMaximumDelay,
                            0),
                        stoppingToken);
                }
            }
        }
        finally
        {
            await DisposeResourcesAsync();
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            await DisposeResourcesAsync();
        }
    }

    private async Task RegisterOrHeartbeatAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshotAccessor.Current;
        if (snapshot is null)
        {
            if (!_runtimeState.HasStagedSnapshot)
            {
                _runtimeState.MarkSnapshotRejected();
            }

            return;
        }

        await _activityLease.EnsureHeldAsync(cancellationToken);
        var dbContext = _dbContext ?? throw new InvalidOperationException("The node database context is unavailable.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var node = await dbContext.Nodes
            .SingleOrDefaultAsync(value => value.NodeId == _options.NodeId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var registered = false;
        if (node is null)
        {
            registered = true;
            node = new Node
            {
                Id = Guid.CreateVersion7(),
                NodeId = _options.NodeId,
                CreatedAt = now,
                Version = 1
            };
            dbContext.Nodes.Add(node);
        }
        else
        {
            node.Version++;
        }

        node.LastHeartbeatAt = now;
        node.LastConfigurationVersion = snapshot.Version;
        node.RuntimeState = _runtimeState.Status.Readiness == HostReadinessState.Ready
            ? "ready"
            : "degraded";
        node.IsActive = true;
        node.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _activityLease.EnsureHeldAsync(cancellationToken);
        _runtimeState.MarkDatabaseAvailable();
        if (registered)
        {
            HostLogMessages.NodeRegistered(_logger, _options.NodeId);
        }
    }

    private async ValueTask DisposeResourcesAsync()
    {
        await _activityLease.DisposeAsync();
        var dbContext = Interlocked.Exchange(ref _dbContext, null);
        if (dbContext is not null)
        {
            await dbContext.DisposeAsync();
        }
    }
}
