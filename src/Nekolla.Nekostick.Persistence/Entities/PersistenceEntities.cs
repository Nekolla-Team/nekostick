using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Persistence.Entities;

/// <summary>Stores the single committed global configuration revision.</summary>
public sealed class ConfigurationRevision
{
    /// <summary>Gets or sets the fixed UUID v7 row identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the fixed singleton key.</summary>
    public string RevisionKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the commit timestamp in UTC.</summary>
    public DateTimeOffset CommittedAt { get; set; }

    /// <summary>Gets or sets safe submitter metadata.</summary>
    public string? CommittedBy { get; set; }

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }
}

/// <summary>Stores the single global settings row.</summary>
public sealed class GlobalSettings
{
    /// <summary>Gets or sets the fixed UUID v7 row identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the inclusive automatic port range start.</summary>
    public int AutoPortRangeStart { get; set; }

    /// <summary>Gets or sets the inclusive automatic port range end.</summary>
    public int AutoPortRangeEnd { get; set; }

    /// <summary>Gets or sets the maximum request body size.</summary>
    public long MaxRequestBodyBytes { get; set; }

    /// <summary>Gets or sets the node concurrent request limit.</summary>
    public int MaxConcurrentRequests { get; set; }

    /// <summary>Gets or sets the configuration poll interval in seconds.</summary>
    public int ConfigurationPollIntervalSeconds { get; set; }

    /// <summary>Gets or sets trusted proxy CIDRs as sensitive JSONB.</summary>
    public string TrustedProxyCidrsJson { get; set; } = "[]";

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets the upstream connection timeout in milliseconds.</summary>
    public int ConnectTimeoutMilliseconds { get; set; }

    /// <summary>Gets or sets the HTTP first-byte and activity timeout in milliseconds.</summary>
    public int HttpActivityTimeoutMilliseconds { get; set; }

    /// <summary>Gets or sets the normal HTTP total timeout in milliseconds.</summary>
    public int HttpTotalTimeoutMilliseconds { get; set; }

    /// <summary>Gets or sets the WebSocket idle timeout in milliseconds.</summary>
    public int WebSocketIdleTimeoutMilliseconds { get; set; }
}

/// <summary>Stores a route and its validated polymorphic target boundary.</summary>
public sealed class Route
{
    /// <summary>Gets or sets the public UUID v7 route identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets whether the route participates in matching.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the matcher kind.</summary>
    public RouteMatcherKind MatcherType { get; set; }

    /// <summary>Gets or sets the matcher pattern.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Gets or sets host constraints as JSONB.</summary>
    public string HostPatternsJson { get; set; } = "[]";

    /// <summary>Gets or sets method constraints as JSONB.</summary>
    public string MethodsJson { get; set; } = "[]";

    /// <summary>Gets or sets the target kind.</summary>
    public RouteTargetKind TargetType { get; set; }

    /// <summary>Gets or sets the stable target reference text.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the referenced service for a microservice target.</summary>
    public Guid? ServiceId { get; set; }

    /// <summary>Gets or sets the absolute root for a static target.</summary>
    public string? StaticRootPath { get; set; }

    /// <summary>Gets or sets the stable extension handler identifier.</summary>
    public string? ExtensionHandlerId { get; set; }

    /// <summary>Gets or sets the route priority.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets the forwarding mode.</summary>
    public ForwardingKind ForwardingMode { get; set; }

    /// <summary>Gets or sets the replacement template.</summary>
    public string? ReplaceTemplate { get; set; }

    /// <summary>Gets or sets request header rewrites as JSONB.</summary>
    public string RequestHeaderRewritesJson { get; set; } = "[]";

    /// <summary>Gets or sets response header rewrites as JSONB.</summary>
    public string ResponseHeaderRewritesJson { get; set; } = "[]";

    /// <summary>Gets or sets extension metadata as JSONB.</summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets the referenced service navigation.</summary>
    public Service? Service { get; set; }
}

/// <summary>Stores the safe process-start and health-check configuration of a service.</summary>
public sealed class Service
{
    /// <summary>Gets or sets the public UUID v7 service identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets whether the service is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the absolute executable path.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets process arguments as JSONB.</summary>
    public string ArgumentListJson { get; set; } = "[]";

    /// <summary>Gets or sets the absolute working directory.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets sensitive environment overrides as JSONB.</summary>
    public string EnvironmentJson { get; set; } = "{}";

    /// <summary>Gets or sets the start policy.</summary>
    public ServiceStartPolicy StartMode { get; set; }

    /// <summary>Gets or sets the restart policy.</summary>
    public ServiceRestartPolicy RestartPolicy { get; set; }

    /// <summary>Gets or sets the health-check kind.</summary>
    public ServiceHealthCheckKind HealthCheckType { get; set; }

    /// <summary>Gets or sets the HTTP health path when applicable.</summary>
    public string? HealthCheckHttpPath { get; set; }

    /// <summary>Gets or sets the health timeout in milliseconds.</summary>
    public int HealthCheckTimeoutMilliseconds { get; set; }

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets routes referencing this service.</summary>
    public ICollection<Route> Routes { get; set; } = new List<Route>();

    /// <summary>Gets or sets port leases referencing this service.</summary>
    public ICollection<PortLease> PortLeases { get; set; } = new List<PortLease>();
}

/// <summary>Stores one installed trusted extension record.</summary>
public sealed class ExtensionRecord
{
    /// <summary>Gets or sets the public UUID v7 extension record identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the unique manifest identifier.</summary>
    public string ExtensionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the installed semantic version text.</summary>
    public string InstalledVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the public extension load state.</summary>
    public ExtensionLoadState LoadState { get; set; }

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets extension settings rows.</summary>
    public ICollection<ExtensionSetting> Settings { get; set; } = new List<ExtensionSetting>();
}

/// <summary>Stores one extension-owned sensitive JSON settings document.</summary>
public sealed class ExtensionSetting
{
    /// <summary>Gets or sets the public UUID v7 settings identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the referenced extension record identifier.</summary>
    public Guid ExtensionRecordId { get; set; }

    /// <summary>Gets or sets the settings schema version.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the sensitive JSONB settings document.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets the extension record navigation.</summary>
    public ExtensionRecord? ExtensionRecord { get; set; }
}

/// <summary>Stores one node registration and heartbeat state.</summary>
public sealed class Node
{
    /// <summary>Gets or sets the public UUID v7 node record identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the unique stable node identifier.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Gets or sets the latest heartbeat timestamp in UTC.</summary>
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>Gets or sets the latest complete configuration version.</summary>
    public long LastConfigurationVersion { get; set; }

    /// <summary>Gets or sets the public runtime state text.</summary>
    public string RuntimeState { get; set; } = "registered";

    /// <summary>Gets or sets whether the node is active.</summary>
    public bool IsActive { get; set; }

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets port leases owned by this node.</summary>
    public ICollection<PortLease> PortLeases { get; set; } = new List<PortLease>();
}

/// <summary>Stores a node-owned service port lease.</summary>
public sealed class PortLease
{
    /// <summary>Gets or sets the public UUID v7 lease identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the owning stable node identifier.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Gets or sets the leased TCP port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the referenced service identifier.</summary>
    public Guid ServiceId { get; set; }

    /// <summary>Gets or sets the lease expiration timestamp in UTC.</summary>
    public DateTimeOffset LeaseExpiresAt { get; set; }

    /// <summary>Gets or sets the last renewal timestamp in UTC.</summary>
    public DateTimeOffset RenewedAt { get; set; }

    /// <summary>Gets or sets the bigint optimistic-concurrency version.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the node navigation.</summary>
    public Node? Node { get; set; }

    /// <summary>Gets or sets the service navigation.</summary>
    public Service? Service { get; set; }
}
