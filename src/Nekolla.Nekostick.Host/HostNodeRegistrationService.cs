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
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private readonly HostTerminationState _terminationState;
    private readonly ILogger<HostNodeRegistrationService> _logger;
    private NekostickDbContext? _dbContext;
    private int _retryAttempt;
    private int _alreadyActiveAttempts;

    /// <summary>Creates the node registration and heartbeat service.</summary>
    public HostNodeRegistrationService(
        IDbContextFactory<NekostickDbContext> dbContextFactory,
        IHostConfigurationSnapshotAccessor snapshotAccessor,
        HostRuntimeState runtimeState,
        HostRuntimeOptions options,
        ILogger<HostNodeRegistrationService> logger,
        IHostNodeActivityLease? activityLease = null,
        IHostApplicationLifetime? applicationLifetime = null,
        HostTerminationState? terminationState = null)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _snapshotAccessor = snapshotAccessor ?? throw new ArgumentNullException(nameof(snapshotAccessor));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _activityLease = activityLease ?? new PostgresHostNodeActivityLease(options);
        _applicationLifetime = applicationLifetime;
        _terminationState = terminationState ?? new HostTerminationState();
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
        catch (HostNodeAlreadyActiveException exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(StartAsync));
            HostLogMessages.HostNodeActivityLost(_logger, _options.NodeId);
            _terminationState.MarkFatal();
            _applicationLifetime?.StopApplication();
            await DisposeResourcesAsync();
            throw;
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
                    _retryAttempt = 0;
                    await Task.Delay(_options.HeartbeatInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(RegisterOrHeartbeatAsync));
                    _runtimeState.MarkDatabaseUnavailable();
                    HostLogMessages.NodeHeartbeatUnavailable(_logger);

                    if (!await RecoverConnectionAsync(stoppingToken))
                    {
                        return;
                    }
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

    private async Task<bool> RecoverConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = HostRetryPolicy.GetDelay(
                _options.ReconnectInitialDelay,
                _options.ReconnectMaximumDelay,
                _retryAttempt++);
            await Task.Delay(delay, cancellationToken);

            try
            {
                await ReconnectAsync(cancellationToken);
                _retryAttempt = 0;
                _alreadyActiveAttempts = 0;
                _runtimeState.MarkDatabaseAvailable();
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HostNodeAlreadyActiveException exception)
            {
                // A dropped connection can leave our own zombie session holding the advisory lock
                // until the server reaps it; grant a bounded number of grace retries (the loop's
                // backoff delay applies) before declaring a real takeover fatal.
                if (++_alreadyActiveAttempts <= 3)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(ReconnectAsync));
                    HostLogMessages.HostNodeActivityContended(_logger, _alreadyActiveAttempts);
                    continue;
                }

                HostLogMessages.FailureDetails(_logger, exception, nameof(ReconnectAsync));
                HostLogMessages.HostNodeActivityLost(_logger, _options.NodeId);
                _terminationState.MarkFatal();
                _applicationLifetime?.StopApplication();
                return false;
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(ReconnectAsync));
                _runtimeState.MarkDatabaseUnavailable();
            }
        }

        return false;
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        var previousContext = Interlocked.Exchange(ref _dbContext, null);
        if (previousContext is not null)
        {
            await previousContext.DisposeAsync();
        }

        var replacementContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await replacementContext.Database.OpenConnectionAsync(cancellationToken);
            await _activityLease.AcquireAsync(
                replacementContext.Database.GetDbConnection(),
                cancellationToken);
            _dbContext = replacementContext;
        }
        catch
        {
            await replacementContext.DisposeAsync();
            throw;
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
        HostLogMessages.NodeHeartbeatUpdated(_logger, _options.NodeId, snapshot.Version);
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

/// <summary>Stores a nonzero host exit request for propagation after graceful shutdown.</summary>
public sealed class HostTerminationState
{
    private int _exitCode;

    /// <summary>Gets the requested process exit code, or zero when no fatal stop was requested.</summary>
    public int ExitCode => Volatile.Read(ref _exitCode);

    internal void MarkFatal(int exitCode = 1) =>
        Interlocked.CompareExchange(ref _exitCode, exitCode, 0);
}
