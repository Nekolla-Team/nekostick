namespace Nekolla.Nekostick.Host;

/// <summary>Describes the fail-closed capabilities of the host runtime.</summary>
public sealed record HostRuntimeStatus
{
    internal HostRuntimeStatus(
        bool snapshotAvailable,
        bool databaseAvailable,
        bool configurationValid,
        bool readOnly)
    {
        SnapshotAvailable = snapshotAvailable;
        DatabaseAvailable = databaseAvailable;
        ConfigurationValid = configurationValid;
        ConfigurationWritesAllowed = !readOnly && snapshotAvailable && databaseAvailable && configurationValid;
        NewLeasesAllowed = snapshotAvailable && databaseAvailable && configurationValid;
        NewServicesAllowed = snapshotAvailable && databaseAvailable && configurationValid;
        Readiness = snapshotAvailable
            ? databaseAvailable && configurationValid ? HostReadinessState.Ready : HostReadinessState.Degraded
            : HostReadinessState.Unready;
    }

    /// <summary>Gets whether a validated complete snapshot is available in memory.</summary>
    public bool SnapshotAvailable { get; }

    /// <summary>Gets whether the database was reachable during the latest runtime operation.</summary>
    public bool DatabaseAvailable { get; }

    /// <summary>Gets whether the latest complete snapshot load was valid.</summary>
    public bool ConfigurationValid { get; }

    /// <summary>Gets whether configuration writes may be attempted.</summary>
    public bool ConfigurationWritesAllowed { get; }

    /// <summary>Gets whether new port leases may be allocated.</summary>
    public bool NewLeasesAllowed { get; }

    /// <summary>Gets whether new services may be started.</summary>
    public bool NewServicesAllowed { get; }

    /// <summary>Gets the safe readiness state.</summary>
    public HostReadinessState Readiness { get; }
}

/// <summary>Identifies the host readiness boundary without exposing database details.</summary>
public enum HostReadinessState
{
    /// <summary>No validated snapshot is available.</summary>
    Unready,

    /// <summary>A snapshot and the backing database are available.</summary>
    Ready,

    /// <summary>A snapshot remains routable while persistence capabilities are disabled.</summary>
    Degraded
}

/// <summary>Tracks database and configuration capability state independently of the snapshot.</summary>
public sealed class HostRuntimeState
{
    private readonly HostConfigurationSnapshotHolder _snapshotHolder;
    private readonly bool _readOnly;
    private int _databaseAvailable;
    private int _databaseUnavailable;
    private int _configurationValid;
    private int _stagedConfigurationWritesAllowed;

    /// <summary>Creates fail-closed runtime state.</summary>
    public HostRuntimeState(HostConfigurationSnapshotHolder snapshotHolder, HostNodeOptions nodeOptions)
    {
        _snapshotHolder = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        ArgumentNullException.ThrowIfNull(nodeOptions);
        _readOnly = nodeOptions.ReadOnly;
    }

    /// <summary>Gets the current safe capability state.</summary>
    public HostRuntimeStatus Status => new(
        _snapshotHolder.Current is not null,
        Volatile.Read(ref _databaseAvailable) == 1,
        Volatile.Read(ref _configurationValid) == 1,
        _readOnly);
    /// <summary>Gets whether host-wide configuration writes may be attempted.</summary>
    public bool ConfigurationWritesAllowed => Status.ConfigurationWritesAllowed;

    /// <summary>Gets whether a current complete, validated configuration snapshot is available.</summary>
    public bool IsReady => _snapshotHolder.Current is not null;

    /// <summary>Gets whether a candidate snapshot is staged without being published.</summary>
    internal bool HasStagedSnapshot => _snapshotHolder.Current is null && _snapshotHolder.HasSnapshot;

    /// <summary>Gets whether extension-scoped configuration writes may be attempted.</summary>
    internal bool ExtensionConfigurationWritesAllowed =>
        ConfigurationWritesAllowed ||
        (!_readOnly &&
            Volatile.Read(ref _stagedConfigurationWritesAllowed) == 1 &&
            Volatile.Read(ref _databaseUnavailable) == 0);

    /// <summary>Gets whether new leases are currently allowed.</summary>
    public bool NewLeasesAllowed => Status.NewLeasesAllowed;

    /// <summary>Gets whether new services are currently allowed.</summary>
    public bool NewServicesAllowed => Status.NewServicesAllowed;

    internal void MarkSnapshotAccepted()
    {
        Volatile.Write(ref _stagedConfigurationWritesAllowed, 0);
        Volatile.Write(ref _databaseUnavailable, 0);
        Volatile.Write(ref _databaseAvailable, 1);
        Volatile.Write(ref _configurationValid, 1);
    }

    internal void BeginStagedConfigurationWrites()
    {
        if (!_readOnly && Volatile.Read(ref _databaseUnavailable) == 0)
        {
            Volatile.Write(ref _stagedConfigurationWritesAllowed, 1);
        }
    }

    internal void EndStagedConfigurationWrites() =>
        Volatile.Write(ref _stagedConfigurationWritesAllowed, 0);


    internal void MarkSnapshotRejected()
    {
        Volatile.Write(ref _stagedConfigurationWritesAllowed, 0);
        Volatile.Write(ref _configurationValid, 0);
    }

    internal void MarkDatabaseAvailable()
    {
        Volatile.Write(ref _databaseUnavailable, 0);
        Volatile.Write(ref _databaseAvailable, 1);
    }

    internal void MarkDatabaseUnavailable()
    {
        Volatile.Write(ref _stagedConfigurationWritesAllowed, 0);
        Volatile.Write(ref _databaseUnavailable, 1);
        Volatile.Write(ref _databaseAvailable, 0);
        Volatile.Write(ref _configurationValid, 0);
    }
}
