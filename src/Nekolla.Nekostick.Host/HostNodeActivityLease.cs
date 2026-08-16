using System.Data;
using System.Data.Common;

namespace Nekolla.Nekostick.Host;

/// <summary>Owns the process-level activity boundary for the host node.</summary>
public interface IHostNodeActivityLease : IAsyncDisposable
{
    /// <summary>Acquires the default-node activity lease before heartbeat work starts.</summary>
    Task AcquireAsync(DbConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Verifies that the PostgreSQL session still owns the activity lease.</summary>
    Task EnsureHeldAsync(CancellationToken cancellationToken = default);
}

/// <summary>Uses a PostgreSQL session advisory lock to serialize active default nodes.</summary>
public sealed class PostgresHostNodeActivityLease : IHostNodeActivityLease
{
    private const long DefaultNodeActivityAdvisoryLockKey = 0x4E454B4E4F444530L;
    private const string AdvisoryLockKeyParameterName = "lock_key";

    private readonly bool _isDefaultNode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DbConnection? _connection;
    private int _disposed;

    /// <summary>Creates the activity lease for the configured node.</summary>
    public PostgresHostNodeActivityLease(HostRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _isDefaultNode = string.Equals(options.NodeId, "0", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task AcquireAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_isDefaultNode)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_connection is not null)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
            AddAdvisoryLockKeyParameter(command);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not bool acquired || !acquired)
            {
                throw new HostNodeAlreadyActiveException();
            }

            _connection = connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsureHeldAsync(CancellationToken cancellationToken = default)
    {
        if (!_isDefaultNode)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var connection = _connection ?? throw new HostNodeActivityLostException();
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_locks
                        WHERE pid = pg_backend_pid()
                          AND locktype = 'advisory'
                          AND granted
                          AND classid = ((@lock_key >> 32) & 4294967295)::oid
                          AND objid = (@lock_key & 4294967295)::oid
                          AND objsubid = 1
                    );
                    """;
                AddAdvisoryLockKeyParameter(command);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                if (result is not bool held || !held)
                {
                    throw new HostNodeActivityLostException();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HostNodeActivityLostException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new HostNodeActivityLostException();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var connection = _connection;
            _connection = null;
            if (connection is null)
            {
                return;
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
                AddAdvisoryLockKeyParameter(command);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Closing the PostgreSQL session also releases a session advisory lock.
            }

            try
            {
                await connection.CloseAsync();
            }
            catch (Exception)
            {
                // The connection is owned by the node service and is disposed with its context.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void AddAdvisoryLockKeyParameter(DbCommand command)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = AdvisoryLockKeyParameterName;
        parameter.DbType = DbType.Int64;
        parameter.Value = DefaultNodeActivityAdvisoryLockKey;
        command.Parameters.Add(parameter);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            nameof(PostgresHostNodeActivityLease));
    }
}

internal sealed class HostNodeAlreadyActiveException : Exception
{
}

internal sealed class HostNodeActivityLostException : Exception
{
}
