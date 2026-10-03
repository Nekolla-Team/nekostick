using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Host;

/// <summary>Contains host-local runtime values derived from validated bootstrap options.</summary>
public sealed record HostRuntimeOptions
{
    /// <summary>Creates runtime options without exposing the connection string through diagnostics.</summary>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="nodeId">The stable node identifier.</param>
    /// <param name="readOnly">Whether configuration writes are disabled.</param>
    /// <param name="serviceLogBufferByteBudget">The per-service retained service-log buffer budget in bytes.</param>
    public HostRuntimeOptions(
        string connectionString,
        string nodeId,
        bool readOnly,
        int serviceLogBufferByteBudget = BootstrapDefaults.DefaultServiceLogBufferByteBudget)
    {
        ConnectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
        NodeId = string.IsNullOrWhiteSpace(nodeId)
            ? throw new ArgumentException("A node identifier is required.", nameof(nodeId))
            : nodeId;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(serviceLogBufferByteBudget, 0);
        ReadOnly = readOnly;
        ServiceLogBufferByteBudget = serviceLogBufferByteBudget;
    }

    /// <summary>Gets the PostgreSQL connection string. Callers must treat it as secret.</summary>
    public string ConnectionString { get; }

    /// <summary>Gets the stable node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets whether configuration writes are disabled for this process.</summary>
    public bool ReadOnly { get; }
    /// <summary>Gets the per-service retained service-log buffer budget in bytes.</summary>
    public int ServiceLogBufferByteBudget { get; }

    /// <summary>Gets the fixed configuration version polling interval.</summary>
    public TimeSpan ConfigurationPollInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the node registration heartbeat interval.</summary>
    public TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the initial reconnect delay for PostgreSQL notification listening.</summary>
    public TimeSpan ReconnectInitialDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum reconnect delay for PostgreSQL notification listening.</summary>
    public TimeSpan ReconnectMaximumDelay { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the PostgreSQL LISTEN channel used for configuration hints.</summary>
    public string ConfigurationNotificationChannel { get; } = "nekostick_config_changed";
}
