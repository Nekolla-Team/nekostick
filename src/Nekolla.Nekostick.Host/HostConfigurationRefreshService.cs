using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

/// <summary>Abstracts the PostgreSQL fast invalidation hint from snapshot refresh logic.</summary>
public interface IConfigurationChangeSignal
{
    /// <summary>Waits for one configuration notification, reconnecting on bounded jittered delays.</summary>
    Task WaitForHintAsync(CancellationToken cancellationToken = default);
}

/// <summary>Uses PostgreSQL LISTEN/NOTIFY as a best-effort fast refresh hint.</summary>
public sealed class PostgresConfigurationChangeSignal : IConfigurationChangeSignal
{
    private readonly HostRuntimeOptions _options;
    private readonly HostRuntimeState? _runtimeState;
    private readonly ILogger<PostgresConfigurationChangeSignal> _logger;

    /// <summary>Creates a PostgreSQL notification listener.</summary>
    public PostgresConfigurationChangeSignal(
        HostRuntimeOptions options,
        HostRuntimeState? runtimeState = null,
        ILogger<PostgresConfigurationChangeSignal>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runtimeState = runtimeState;
        _logger = logger ?? NullLogger<PostgresConfigurationChangeSignal>.Instance;
    }

    /// <inheritdoc />
    public async Task WaitForHintAsync(CancellationToken cancellationToken = default)
    {
        var retryAttempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new NpgsqlConnection(_options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"LISTEN {QuoteChannel(_options.ConfigurationNotificationChannel)};";
                await command.ExecuteNonQueryAsync(cancellationToken);

                await connection.WaitAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(PostgresConfigurationChangeSignal.WaitForHintAsync));
                _runtimeState?.MarkDatabaseUnavailable();
                var delay = HostRetryPolicy.GetDelay(
                    _options.ReconnectInitialDelay,
                    _options.ReconnectMaximumDelay,
                    retryAttempt++);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static string QuoteChannel(string channel) =>
        '"' + channel.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

/// <summary>Refreshes the immutable snapshot from NOTIFY hints and a fixed 30-second version poll.</summary>
public sealed class HostConfigurationRefreshService : BackgroundService
{
    private readonly HostConfigurationSnapshotHolder _snapshotAccessor;
    private readonly IHostConfigurationSnapshotReader _snapshotReader;
    private readonly IConfigurationChangeSignal _changeSignal;
    private readonly HostRuntimeState _runtimeState;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HostRuntimeOptions _options;
    private readonly HostConfigurationPublisher _publisher;
    private readonly ILogger<HostConfigurationRefreshService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Creates the runtime configuration refresh service.</summary>
    public HostConfigurationRefreshService(
        HostConfigurationSnapshotHolder snapshotHolder,
        IHostConfigurationSnapshotReader snapshotReader,
        IConfigurationChangeSignal changeSignal,
        HostRuntimeState runtimeState,
        IServiceScopeFactory scopeFactory,
        HostRuntimeOptions options,
        HostConfigurationPublisher publisher,
        ILogger<HostConfigurationRefreshService> logger)
    {
        _snapshotAccessor = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
        _changeSignal = changeSignal ?? throw new ArgumentNullException(nameof(changeSignal));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollTask = PollLoopAsync(stoppingToken);
        var hintTask = HintLoopAsync(stoppingToken);
        await Task.WhenAll(pollTask, hintTask);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.ConfigurationPollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(PollLoopAsync));
                _runtimeState.MarkDatabaseUnavailable();
                HostLogMessages.ConfigurationRefreshUnavailable(_logger);
            }
        }
    }

    private async Task HintLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _changeSignal.WaitForHintAsync(cancellationToken);
                await RefreshAsync(cancellationToken, forceSnapshotReload: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(HintLoopAsync));
                _runtimeState.MarkDatabaseUnavailable();
                HostLogMessages.ConfigurationRefreshUnavailable(_logger);
            }
        }
    }

    private async Task RefreshAsync(
        CancellationToken cancellationToken,
        bool forceSnapshotReload = false)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(forceSnapshotReload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(
        bool forceSnapshotReload,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var revisionReader = scope.ServiceProvider.GetRequiredService<IConfigurationRevisionReader>();
        ConfigurationReadResult<ConfigurationRevisionStatus> revision;
        try
        {
            revision = await revisionReader.ReadCurrentAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (!revision.IsSuccess || revision.Value is null)
        {
            _runtimeState.MarkDatabaseUnavailable();
            HostLogMessages.ConfigurationRevisionUnavailable(_logger, "Configuration storage is unavailable.");
            return;
        }

        var current = _snapshotAccessor.Current;
        if (!forceSnapshotReload && current is not null && current.Version == revision.Value.Version)
        {
            _runtimeState.MarkDatabaseAvailable();
            _runtimeState.MarkSnapshotAccepted();
            return;
        }

        if (!forceSnapshotReload && current is not null && current.Version > revision.Value.Version)
        {
            _runtimeState.MarkDatabaseAvailable();
            _runtimeState.MarkSnapshotRejected();
            HostLogMessages.ConfigurationSnapshotRejected(_logger);
            return;
        }

        var loaded = await _snapshotReader.ReadCompleteAsync(cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            _runtimeState.MarkSnapshotRejected();
            if (loaded.Errors.Any(error => error.Code == ConfigurationErrorCode.StorageUnavailable))
            {
                _runtimeState.MarkDatabaseUnavailable();
                HostLogMessages.ConfigurationRefreshUnavailable(_logger);
            }
            else
            {
                HostLogMessages.ConfigurationSnapshotRejected(_logger);
            }

            return;
        }

        if (await _publisher.PublishAsync(loaded.Value, cancellationToken).ConfigureAwait(false))
        {
            _runtimeState.MarkSnapshotAccepted();
        }
        else
        {
            _runtimeState.MarkSnapshotRejected();
            HostLogMessages.ConfigurationSnapshotRejected(_logger);
        }
    }
}

/// <summary>Computes bounded exponential reconnect delays with bounded jitter.</summary>
internal static class HostRetryPolicy
{
    internal static TimeSpan GetDelay(TimeSpan initial, TimeSpan maximum, int attempt)
    {
        var boundedAttempt = Math.Clamp(attempt, 0, 30);
        var multiplier = Math.Pow(2, boundedAttempt);
        var rawMilliseconds = Math.Min(maximum.TotalMilliseconds, initial.TotalMilliseconds * multiplier);
        var jitteredMilliseconds = rawMilliseconds * (0.8 + Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(Math.Clamp(jitteredMilliseconds, 1, maximum.TotalMilliseconds));
    }
}
