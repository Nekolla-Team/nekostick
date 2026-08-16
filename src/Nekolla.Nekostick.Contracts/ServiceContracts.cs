using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Specifies when a service is started by a node.</summary>
public enum ServiceStartMode
{
    /// <summary>Start after the node applies its configuration.</summary>
    Eager,

    /// <summary>Start on the first request that requires the service.</summary>
    Lazy
}

/// <summary>Specifies the service restart policy.</summary>
public enum ServiceRestartPolicy
{
    /// <summary>Never restart an exited service.</summary>
    Never,

    /// <summary>Restart after an unsuccessful exit.</summary>
    OnFailure,

    /// <summary>Restart after any exit.</summary>
    Always
}

/// <summary>Specifies the health check mechanism for a service.</summary>
public enum ServiceHealthCheckType
{
    /// <summary>Check that the process is alive.</summary>
    Process,

    /// <summary>Check a loopback TCP endpoint.</summary>
    Tcp,

    /// <summary>Check a configured HTTP endpoint.</summary>
    Http
}

/// <summary>Defines a service health-check boundary without framework types.</summary>
public sealed record ServiceHealthCheckConfiguration
{
    /// <summary>Creates service health-check settings.</summary>
    /// <param name="type">The health-check type.</param>
    /// <param name="httpPath">The HTTP path for an HTTP check.</param>
    /// <param name="timeout">The maximum check duration.</param>
    public ServiceHealthCheckConfiguration(ServiceHealthCheckType type, string? httpPath, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (type == ServiceHealthCheckType.Http && string.IsNullOrWhiteSpace(httpPath))
        {
            throw new ArgumentException("HTTP health checks require a path.", nameof(httpPath));
        }

        Type = type;
        HttpPath = httpPath;
        Timeout = timeout;
    }

    /// <summary>Gets the health-check type.</summary>
    public ServiceHealthCheckType Type { get; }

    /// <summary>Gets the HTTP path when applicable.</summary>
    public string? HttpPath { get; }

    /// <summary>Gets the check timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>Defines the safe process-start subset exposed to configuration.</summary>
public sealed record ServiceConfiguration
{
    /// <summary>Creates a service configuration DTO.</summary>
    /// <param name="id">The public service identifier.</param>
    /// <param name="enabled">Whether the service may run.</param>
    /// <param name="fileName">The absolute executable path.</param>
    /// <param name="argumentList">The immutable argument list.</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <param name="environment">Environment overrides, potentially sensitive.</param>
    /// <param name="startMode">The service start mode.</param>
    /// <param name="restartPolicy">The restart policy.</param>
    /// <param name="healthCheck">The health-check settings.</param>
    /// <param name="createdAt">The UTC creation timestamp.</param>
    /// <param name="updatedAt">The UTC update timestamp.</param>
    /// <param name="version">The optimistic-concurrency version.</param>
    public ServiceConfiguration(
        Guid id,
        bool enabled,
        string fileName,
        ImmutableArray<string> argumentList,
        string workingDirectory,
        ImmutableDictionary<string, string> environment,
        ServiceStartMode startMode,
        ServiceRestartPolicy restartPolicy,
        ServiceHealthCheckConfiguration healthCheck,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = IdentityValidation.RequireUuidV7(id, nameof(id));
        Enabled = enabled;
        if (string.IsNullOrWhiteSpace(fileName) || !Path.IsPathRooted(fileName))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(fileName));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
        {
            throw new ArgumentException("An absolute working directory is required.", nameof(workingDirectory));
        }

        FileName = fileName;
        ArgumentList = argumentList.IsDefault ? ImmutableArray<string>.Empty : argumentList;
        WorkingDirectory = workingDirectory;
        Environment = environment ?? ImmutableDictionary<string, string>.Empty;
        StartMode = startMode;
        RestartPolicy = restartPolicy;
        HealthCheck = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        Version = version < 0 ? throw new ArgumentOutOfRangeException(nameof(version)) : version;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets whether the service is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the absolute executable path.</summary>
    public string FileName { get; }

    /// <summary>Gets the immutable process arguments.</summary>
    public ImmutableArray<string> ArgumentList { get; }

    /// <summary>Gets the absolute working directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets environment overrides. Consumers must treat values as sensitive.</summary>
    public ImmutableDictionary<string, string> Environment { get; }

    /// <summary>Gets the start mode.</summary>
    public ServiceStartMode StartMode { get; }

    /// <summary>Gets the restart policy.</summary>
    public ServiceRestartPolicy RestartPolicy { get; }

    /// <summary>Gets the health-check settings.</summary>
    public ServiceHealthCheckConfiguration HealthCheck { get; }

    /// <summary>Gets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets the optimistic-concurrency version.</summary>
    public long Version { get; }
}

/// <summary>Defines immutable global business settings.</summary>
public sealed record GlobalSettingsConfiguration
{
    /// <summary>Creates global settings with the stage-A defaults.</summary>
    /// <param name="version">The optimistic-concurrency version.</param>
    /// <param name="autoPortRangeStart">The inclusive automatic port range start.</param>
    /// <param name="autoPortRangeEnd">The inclusive automatic port range end.</param>
    /// <param name="maxRequestBodyBytes">The maximum request body size.</param>
    /// <param name="maxConcurrentRequests">The node concurrency limit.</param>
    /// <param name="configurationPollInterval">The configuration version poll interval.</param>
    /// <param name="trustedProxyCidrs">The immutable trusted proxy CIDR list.</param>
    public GlobalSettingsConfiguration(
        long version = 0,
        int autoPortRangeStart = 20000,
        int autoPortRangeEnd = 29999,
        long maxRequestBodyBytes = 30 * 1024 * 1024,
        int maxConcurrentRequests = 1024,
        TimeSpan? configurationPollInterval = null,
        ImmutableArray<string> trustedProxyCidrs = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        ArgumentOutOfRangeException.ThrowIfLessThan(autoPortRangeStart, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(autoPortRangeStart, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(autoPortRangeEnd, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(autoPortRangeEnd, 65535);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(autoPortRangeStart, autoPortRangeEnd);

        if (maxRequestBodyBytes <= 0 || maxConcurrentRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequestBodyBytes));
        }

        var interval = configurationPollInterval ?? TimeSpan.FromSeconds(30);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configurationPollInterval));
        }

        Version = version;
        AutoPortRangeStart = autoPortRangeStart;
        AutoPortRangeEnd = autoPortRangeEnd;
        MaxRequestBodyBytes = maxRequestBodyBytes;
        MaxConcurrentRequests = maxConcurrentRequests;
        ConfigurationPollInterval = interval;
        TrustedProxyCidrs = trustedProxyCidrs.IsDefault
            ? ImmutableArray<string>.Empty
            : trustedProxyCidrs;
    }

    /// <summary>Gets the global optimistic-concurrency version.</summary>
    public long Version { get; }

    /// <summary>Gets the inclusive automatic port range start.</summary>
    public int AutoPortRangeStart { get; }

    /// <summary>Gets the inclusive automatic port range end.</summary>
    public int AutoPortRangeEnd { get; }

    /// <summary>Gets the request body limit in bytes.</summary>
    public long MaxRequestBodyBytes { get; }

    /// <summary>Gets the maximum concurrent request count.</summary>
    public int MaxConcurrentRequests { get; }

    /// <summary>Gets the configuration poll interval.</summary>
    public TimeSpan ConfigurationPollInterval { get; }

    /// <summary>Gets the immutable trusted proxy CIDR list.</summary>
    public ImmutableArray<string> TrustedProxyCidrs { get; }
}
