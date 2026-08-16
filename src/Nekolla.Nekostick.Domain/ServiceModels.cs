using System.Collections.Immutable;

namespace Nekolla.Nekostick.Domain;

/// <summary>Describes service lifecycle state.</summary>
public enum ServiceLifecycleState
{
    /// <summary>The service is disabled.</summary>
    Disabled,

    /// <summary>The service is starting.</summary>
    Starting,

    /// <summary>The service is running.</summary>
    Running,

    /// <summary>The service is stopping.</summary>
    Stopping,

    /// <summary>The service failed to start or remain healthy.</summary>
    Failed
}

/// <summary>Describes service health state.</summary>
public enum ServiceHealthState
{
    /// <summary>No health result is available.</summary>
    Unknown,

    /// <summary>The latest health check succeeded.</summary>
    Healthy,

    /// <summary>The latest health check failed.</summary>
    Unhealthy
}

/// <summary>Describes when a service is started.</summary>
public enum ServiceStartPolicy
{
    /// <summary>Start during configuration application.</summary>
    Eager,

    /// <summary>Start on demand.</summary>
    Lazy
}

/// <summary>Describes service restart behavior.</summary>
public enum ServiceRestartPolicy
{
    /// <summary>Never restart.</summary>
    Never,

    /// <summary>Restart after failure.</summary>
    OnFailure,

    /// <summary>Restart after every exit.</summary>
    Always
}

/// <summary>Describes supported service health mechanisms.</summary>
public enum ServiceHealthCheckKind
{
    /// <summary>Check only process liveness.</summary>
    Process,

    /// <summary>Check a loopback TCP listener.</summary>
    Tcp,

    /// <summary>Check an HTTP path.</summary>
    Http
}

/// <summary>Identifies the permitted loopback address family.</summary>
public enum LoopbackAddressKind
{
    /// <summary>IPv4 loopback 127.0.0.1.</summary>
    IPv4,

    /// <summary>IPv6 loopback ::1.</summary>
    IPv6
}

/// <summary>Contains a validated loopback port endpoint.</summary>
public readonly record struct LoopbackEndpoint
{
    /// <summary>Creates a loopback endpoint.</summary>
    /// <param name="address">The permitted loopback address family.</param>
    /// <param name="port">The TCP port.</param>
    public LoopbackEndpoint(LoopbackAddressKind address, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Address = address;
        Port = port;
    }

    /// <summary>Gets the loopback address family.</summary>
    public LoopbackAddressKind Address { get; }

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; }
}

/// <summary>Contains immutable health-check settings.</summary>
public sealed record HealthCheckDefinition
{
    /// <summary>Creates health-check settings.</summary>
    /// <param name="kind">The health-check kind.</param>
    /// <param name="timeout">The maximum check duration.</param>
    /// <param name="httpPath">The path for an HTTP check.</param>
    public HealthCheckDefinition(ServiceHealthCheckKind kind, TimeSpan timeout, string? httpPath = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (kind == ServiceHealthCheckKind.Http && string.IsNullOrWhiteSpace(httpPath))
        {
            throw new ArgumentException("HTTP health checks require a path.", nameof(httpPath));
        }

        Kind = kind;
        Timeout = timeout;
        HttpPath = httpPath;
    }

    /// <summary>Gets the health-check kind.</summary>
    public ServiceHealthCheckKind Kind { get; }

    /// <summary>Gets the health-check timeout.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the HTTP path when applicable.</summary>
    public string? HttpPath { get; }
}

/// <summary>Represents a domain service definition without process framework types.</summary>
public sealed class ServiceDefinition : EntityBase
{
    /// <summary>Creates a service definition.</summary>
    /// <param name="uuidGenerator">The UUID v7 generator.</param>
    /// <param name="fileName">The absolute executable path.</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <param name="arguments">The immutable process arguments.</param>
    /// <param name="environment">The immutable environment overrides.</param>
    /// <param name="startPolicy">The startup policy.</param>
    /// <param name="restartPolicy">The restart policy.</param>
    /// <param name="healthCheck">The health-check definition.</param>
    /// <param name="timeProvider">The UTC time provider.</param>
    public ServiceDefinition(
        IUuidV7Generator uuidGenerator,
        string fileName,
        string workingDirectory,
        ImmutableArray<string> arguments,
        ImmutableDictionary<string, string> environment,
        ServiceStartPolicy startPolicy,
        ServiceRestartPolicy restartPolicy,
        HealthCheckDefinition healthCheck,
        TimeProvider? timeProvider = null)
        : base(uuidGenerator, timeProvider)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !Path.IsPathRooted(fileName))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(fileName));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
        {
            throw new ArgumentException("An absolute working directory is required.", nameof(workingDirectory));
        }

        FileName = fileName;
        WorkingDirectory = workingDirectory;
        Arguments = arguments.IsDefault ? ImmutableArray<string>.Empty : arguments;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        StartPolicy = startPolicy;
        RestartPolicy = restartPolicy;
        HealthCheck = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
    }

    /// <summary>Gets the absolute executable path.</summary>
    public string FileName { get; }

    /// <summary>Gets the absolute working directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets immutable process arguments.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>Gets environment overrides, which may contain secrets.</summary>
    public ImmutableDictionary<string, string> Environment { get; }

    /// <summary>Gets the start policy.</summary>
    public ServiceStartPolicy StartPolicy { get; }

    /// <summary>Gets the restart policy.</summary>
    public ServiceRestartPolicy RestartPolicy { get; }

    /// <summary>Gets the health-check definition.</summary>
    public HealthCheckDefinition HealthCheck { get; }
}

/// <summary>Describes a service's observable lifecycle and health state.</summary>
public readonly record struct ServiceRuntimeState
{
    /// <summary>Creates a runtime state.</summary>
    /// <param name="lifecycle">The lifecycle state.</param>
    /// <param name="health">The health state.</param>
    /// <param name="restartCount">The restart count in the active window.</param>
    public ServiceRuntimeState(ServiceLifecycleState lifecycle, ServiceHealthState health, int restartCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(restartCount);

        Lifecycle = lifecycle;
        Health = health;
        RestartCount = restartCount;
    }

    /// <summary>Gets the lifecycle state.</summary>
    public ServiceLifecycleState Lifecycle { get; }

    /// <summary>Gets the health state.</summary>
    public ServiceHealthState Health { get; }

    /// <summary>Gets the active-window restart count.</summary>
    public int RestartCount { get; }
}
